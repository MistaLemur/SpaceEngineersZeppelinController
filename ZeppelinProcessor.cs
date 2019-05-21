using ModNetworkAPI;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace ZepController
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class ZeppelinProcessor : MySessionComponentBase
    {
        private const string balloonName = "Cell";
        private const string ballastName = "Tank";
        private const string exhaustName = "Vent";
        private const string LCDname = "Zeppelin";

        public NetworkAPI Network => NetworkAPI.Instance;

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            if (!NetworkAPI.IsInitialized)
            {
                NetworkAPI.Init(Core.ModId, Core.ModName);
            }
        }

        public override void UpdateBeforeSimulation()
        {
            foreach (ZeppelinDefinition def in ZeppelinController.ZeppelinGridData.Values)
            {
                if (def.Grid == null) continue; // something went wrong

            }
        }

        public void ZeppSetup(ZeppelinDefinition def)
        {
            //isSetup = false;
            //hasLCD = false;
            //hasExhaust = false;
            //dockFilled = false;
            //isDocked = false;
            //loadedConfig = "";

            List<IMySlimBlock> blocksList = new List<IMySlimBlock>();
            def.Grid.GetBlocks(blocksList, b => b.FatBlock is IMyTerminalBlock);
            //ModBlock.CubeGrid.GetBlocks(blocksList, b => b.FatBlock is IMyTerminalBlock);

            def.Balloons.Clear();
            def.Ballasts.Clear();
            def.OxygenFarms.Clear();
            def.Exhaust.Clear();
            def.Gears.Clear();
            def.Connectors.Clear();
            //def.OtherCockpits.Clear();
            def.Gyros.Clear();

            foreach (IMySlimBlock slim in blocksList)
            {
                IMyCubeBlock fat = slim.FatBlock;
                if (!(fat is IMyTerminalBlock)) continue;

                IMyTerminalBlock block = fat as IMyTerminalBlock;

                if (block is IMyGasTank)
                {
                    if (block.CustomName.Contains(balloonName)) // && block.BlockDefinition.SubtypeId == "BaloonTank"
                    {
                        def.Balloons.Add(block as IMyGasTank);
                    }
                    else if (block.CustomName.Contains(ballastName))
                    {
                        def.Ballasts.Add(block as IMyGasTank);
                    }

                }
                else if (block is IMyOxygenFarm)
                {
                    def.OxygenFarms.Add(block as IMyOxygenFarm);
                }
                else if (block is IMyThrust)
                {
                    if (block.CustomName.Contains(exhaustName))
                        def.Exhaust.Add(block as IMyThrust);
                }
                else if (block is IMyLandingGear)
                {
                    def.Gears.Add(block as IMyLandingGear);
                }
                else if (block is IMyShipConnector)
                {
                    def.Connectors.Add(block as IMyShipConnector);
                }
                else if (block is IMyTextPanel)
                {
                    IMyTextPanel textPanel = block as IMyTextPanel;
                    if (textPanel.GetPublicTitle().Contains(LCDname))
                        def.LCDs.Add(textPanel);
                }
                //else if (block is IMyCockpit && !block.BlockDefinition.IsNull() && block.BlockDefinition.SubtypeId == "CockpitOpen")
                //{
                //    otherCockpits.Add(block as IMyCockpit);
                //}
                else if (block is IMyGyro)
                {
                    def.Gyros.Add(block as IMyGyro);
                }
            }

            //if (ModBlock.CustomData.Length == 0)
            //{
            //    //init PID parameters into the modblock
            //    WriteNewConfig();
            //}
            //else if (ModBlock.CustomData != loadedConfig)
            //{
            //    //parse CustomData for PID parameters.
            //    ParseConfig(ModBlock.CustomData, "Altitude", "Pitch", "Roll");
            //}

            //reset PID states
            ResetPIDState();

            ResetTargetElevation();

            //Init desired altitude
            if (def.Balloons.Count > 0) // && def.Ballasts.Count > 0
            {
                def.IsSetup = true;

                if (def.Exhaust.Count > 0) hasExhaust = true;
                if (lcd != null) hasLCD = true;

                ToggleExhaust(exhaust, false);
                ToggleGasStockpile(balloons, false);
                ToggleGasStockpile(ballasts, false);

                if (printDebug)
                {
                    lcdText.Clear();
                    lcdText.Append("RAN AN UPDATE SETUP\n");
                    lcdText.Append("# balls " + balloons.Count + "\n");
                    lcdText.Append("# tanks " + ballasts.Count + "\n");
                    lcdText.Append("# vents " + exhaust.Count + "\n");
                    lcdText.Append("" + MyAPIGateway.Session?.LocalHumanPlayer?.Character?.ControllerInfo?.Controller?.ControlledEntity?.Entity?.GetType() + "\n");

                    UpdateLCD();
                }

                ToggleGyroOnOff(Data.IsActive);
            }
        }

    }
}
