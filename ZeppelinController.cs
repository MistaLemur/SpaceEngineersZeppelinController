using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ZepController
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Cockpit), false, "CockpitOpen")]
    public class ZeppelinController : MyGameLogicComponent
    {
        public const float UP_1 = (1f / 1000f);
        public const float DOWN_1 = (-1f / 1000f);
        public const float UP_10 = (10f / 1000f);
        public const float DOWN_10 = (-10f / 1000f);
        public const float UP_100 = (100f / 1000f);
        public const float DOWN_100 = (-100f / 1000f);

        public ZeppelinData Data { get; set; } = null;
        public IMyCockpit ModBlock { get; private set; } = null;

        private bool ControlsInitialized = false;

        private bool isSetup = false;
        private bool isDocked = false;
        private bool hasExhaust = false;
        private bool hasLCD = false;

        private bool dockFilled = false;

        private bool useExhaust = true;
        private bool useGenerator = false;

        private int msElapsed;
        private double sElapsed;

        private const string balloonName = "Cell";
        private const string ballastName = "Tank";
        private const string exhaustName = "Vent";
        private const string LCDname = "Zeppelin";

        private List<IMyGasTank> balloons = new List<IMyGasTank>();
        private List<IMyGasTank> ballasts = new List<IMyGasTank>();

        private List<IMyOxygenFarm> oxygenFarms = new List<IMyOxygenFarm>();
        private List<IMyThrust> exhaust = new List<IMyThrust>();

        private List<IMyLandingGear> gears = new List<IMyLandingGear>();
        private List<IMyShipConnector> connectors = new List<IMyShipConnector>();

        private List<IMyCockpit> otherCockpits = new List<IMyCockpit>();

        private IMyTextPanel lcd;

        private const double gravity = 9.81;

        private const double maxTankRatio = 0.98;
        private const double minTankRatio = 0.5;

        private PID AltitudeController = new PID(0.5, 1, 25);
        private PID PitchController = new PID(0.5, 0.1, 0.25);
        private PID RollController = new PID(0.5, 0.1, 0.25);

        private double currentAltitude = 0;
        private double lastAltitude = 0;
        private double lastUpwardsVelocity = 0;

        //private const double balloonForce = 755194.1960563; // k = 200 * 1000 / 200;
        private const double balloonForce = 1137782;//1154500;// k = 300 * 1000 / 200;
                                                    //1121064
                                                    //901650

        private const double ERROR_MARGIN = 0.0010;

        private double lcdUpdateCounter = 0.25;
        private double lcdUpdateDelay = 0.5;

        private string lcdText = "";

        private string loadedConfig = "";

        private double feedBackThreshhold = 0.35;

        private bool printDebug = false;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            ModBlock = Entity as IMyCockpit;
            Data = new ZeppelinData() { BlockId = Entity.EntityId, TargetAltitude = 3.5f };

            Core.RegisterZeppelin(this);

            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.EACH_FRAME; // this is how you add flags to run the functions below
            loadedConfig = "";
        }

        public override void Close()
        {
            Core.UnregisterZeppelin(this);

            NeedsUpdate = VRage.ModAPI.MyEntityUpdateEnum.NONE;
        }

        public void ZeppSetup()
        {
            ModBlock = Entity as IMyCockpit;

            isSetup = false;
            hasLCD = false;
            hasExhaust = false;

            loadedConfig = "";

            if (ModBlock == null) return;
            if (!IsReal()) return;

            List<IMySlimBlock> blocksList = new List<IMySlimBlock>();
            ModBlock.CubeGrid.GetBlocks(blocksList, b => b.FatBlock is IMyTerminalBlock);

            balloons.Clear();
            ballasts.Clear();
            oxygenFarms.Clear();
            exhaust.Clear();
            gears.Clear();
            connectors.Clear();
            otherCockpits.Clear();

            foreach (IMySlimBlock slim in blocksList)
            {
                IMyCubeBlock fat = slim.FatBlock;
                if (!(fat is IMyTerminalBlock)) continue;

                IMyTerminalBlock block = fat as IMyTerminalBlock;
                if (block == ModBlock) continue;

                if (block is IMyGasTank)
                {
                    if (block.CustomName.Contains(balloonName))
                        balloons.Add(block as IMyGasTank);
                    else if (block.CustomName.Contains(ballastName))
                        ballasts.Add(block as IMyGasTank);

                }
                else if (block is IMyOxygenFarm)
                {
                    oxygenFarms.Add(block as IMyOxygenFarm);
                }
                else if (block is IMyThrust)
                {
                    if (block.CustomName.Contains(exhaustName))
                        exhaust.Add(block as IMyThrust);
                }
                else if (block is IMyLandingGear)
                {
                    gears.Add(block as IMyLandingGear);
                }
                else if (block is IMyShipConnector)
                {
                    connectors.Add(block as IMyShipConnector);
                }
                else if (block is IMyTextPanel)
                {
                    IMyTextPanel textPanel = block as IMyTextPanel;
                    if (textPanel.GetPublicTitle().Contains(LCDname))
                        lcd = textPanel;
                }
                else if (block is IMyCockpit)
                {
                    IMyCockpit cock = block as IMyCockpit;
                    otherCockpits.Add(cock);
                }
            }

            if (ModBlock.CustomData.Length == 0)
            {
                //init PID parameters into the modblock
                WriteNewConfig();
            }
            else if (ModBlock.CustomData != loadedConfig)
            {
                //parse CustomData for PID parameters.
                ParseConfig(ModBlock.CustomData, "Altitude", "Pitch", "Roll");
            }

            //reset PID states
            ResetPIDState();

            ResetTargetElevation();

            //Init desired altitude
            if (balloons.Count > 0 && ballasts.Count > 0)
            {
                isSetup = true;

                if (exhaust.Count > 0) hasExhaust = true;
                if (lcd != null) hasLCD = true;

                ToggleExhaust(exhaust, false);
                ToggleGasStockpile(balloons, false);
                ToggleGasStockpile(ballasts, false);


                NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.EACH_FRAME; // this is how you add flags to run the functions below
                if (printDebug)
                {

                    lcdText = "RAN AN UPDATE SETUP\n";
                    //lcdText += "" + NeedsUpdate + "\n";
                    lcdText += "# balls " + balloons.Count + "\n";
                    lcdText += "# tanks " + ballasts.Count + "\n";
                    lcdText += "# vents " + exhaust.Count + "\n";
                    lcdText += "serverupdate " + ShouldServerUpdate() + "\n";
                    lcdText += "clientupdate " + ShouldClientUpdate() + "\n";
                    lcdText += "" + MyAPIGateway.Session?.LocalHumanPlayer?.Character?.ControllerInfo?.Controller?.ControlledEntity?.Entity?.GetType() + "\n";

                    UpdateLCD();
                }

                Core.RegisterZeppelin(this);
            }
        }

        private void CreateControls()
        {
            if (ControlsInitialized) return;
            ControlsInitialized = true;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                IMyTerminalControlSlider Slider = null;
                IMyTerminalControlButton Button = null;
                IMyTerminalAction Action = null;
                IMyTerminalControlOnOffSwitch OnOff = null;

                OnOff = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyCockpit>("Zeppelin Controller On/Off");
                OnOff.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                OnOff.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                OnOff.Getter = (block) => Data.IsActive;
                OnOff.Setter = (block, value) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "toggle_active", DataType = block.EntityId.ToString() });
                    }
                    else
                    {
                        (block.GameLogic as ZeppelinController).ToggleActive();
                    }
                };
                OnOff.Title = MyStringId.GetOrCompute("Zeppelin Controller On/Off");
                OnOff.OnText = MyStringId.GetOrCompute("On");
                OnOff.OffText = MyStringId.GetOrCompute("Off");
                MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(OnOff);

                Slider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyCockpit>("Zeppelin Altitude");
                Slider.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Slider.Setter = (block, value) =>
                {
                    (block.GameLogic as ZeppelinController).Data.TargetAltitude = value;
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendDataChanged(Data);
                    }
                };
                Slider.Getter = (block) => Data.TargetAltitude;
                Slider.Writer = (block, value) => value.Append($"{Data.TargetAltitude.ToString("n3")} km");
                Slider.Title = MyStringId.GetOrCompute("Zeppelin Altitude");
                Slider.Tooltip = MyStringId.GetOrCompute("km Distance above sea level");
                Slider.SetLimits(0, 20);
                MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(Slider);

                Button = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyCockpit>("Update Zeppelin Setup");
                Button.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                Button.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                Button.Title = MyStringId.GetOrCompute("Update Zeppelin Setup");
                Button.Tooltip = MyStringId.GetOrCompute("Use to update controller blocks & config");
                Button.Action = (b) =>
                {
                    if (ShouldRunUpdates())
                    {
                        (b.GameLogic as ZeppelinController).ZeppSetup();
                    }
                    else
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "run", DataType = b.EntityId.ToString() });
                    }
                };
                MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(Button);

                //========= Actions ==========

                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Climb 1m");
                Action.Name.Append($"Zeppelin Climb 1m");
                Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = UP_1.ToString() });
                    }
                    else
                    {
                        (b.GameLogic as ZeppelinController).ChangeTargetElevation(UP_1);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);


                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Drop 1m");
                Action.Name.Append($"Zeppelin Drop 1m");
                Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = DOWN_1.ToString() });
                    }
                    else
                    {
                        (b.GameLogic as ZeppelinController).ChangeTargetElevation(DOWN_1);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);


                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Climb 10m");
                Action.Name.Append($"Zeppelin Climb 10m");
                Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = UP_10.ToString() });
                    }
                    else
                    {
                        (b.GameLogic as ZeppelinController).ChangeTargetElevation(UP_10);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);


                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Drop 10m");
                Action.Name.Append($"Zeppelin Drop 10m");
                Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = DOWN_10.ToString() });
                    }
                    else
                    {
                        (b.GameLogic as ZeppelinController).ChangeTargetElevation(DOWN_10);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);

                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Climb 100m");
                Action.Name.Append($"Zeppelin Climb 100m");
                Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = UP_100.ToString() });
                    }
                    else
                    {
                        (b.GameLogic as ZeppelinController).ChangeTargetElevation(UP_100);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);


                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Drop 100m");
                Action.Name.Append($"Zeppelin Drop 100m");
                Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = DOWN_100.ToString() });
                    }
                    else
                    {
                        (b.GameLogic as ZeppelinController).ChangeTargetElevation(DOWN_100);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);


                Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Set Current Altitude");
                Action.Name.Append($"Set Current Altitude");
                Action.Writer = (b, str) => str.Append($"Set Current Altitude");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "restart", DataType = b.EntityId.ToString() });
                    }
                    else
                    {
                        ((ZeppelinController)b.GameLogic).ResetTargetElevation();
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Action);


                Action = MyAPIGateway.TerminalControls.CreateAction<Sandbox.ModAPI.Ingame.IMyCockpit>("ZeppelinControllerOn/Off");
                Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "toggle_active", DataType = b.EntityId.ToString() });
                    }
                    else
                    {
                        ((ZeppelinController)b.GameLogic).ToggleActive();
                    }
                };
                Action.Name = new System.Text.StringBuilder("Zeppelin Controller On/Off");
                Action.Writer = ActiveHotbarText;
                MyAPIGateway.TerminalControls.AddAction<Sandbox.ModAPI.Ingame.IMyCockpit>(Action);

            }
        }

        public override void UpdateOnceBeforeFrame()
        {
            if (ModBlock == null) return;

            if (!ControlsInitialized)
            {
                CreateControls();
            }

            if (ShouldServerUpdate())
            {
                // put your start actions here
                if (!isSetup)
                {
                    ZeppSetup();
                    Core.SendDataChanged(Data);
                }
            }

        }

        public override void UpdateBeforeSimulation()
        {

            if (ModBlock == null) return;
            if (!IsReal()) return;

            // turn off zeppelin controller if this is not the main
            if (MyAPIGateway.Multiplayer.IsServer && Data.IsActive && otherCockpits.Count > 0 && !ModBlock.IsMainCockpit)
            {
                Data.IsActive = false;
                Core.SendDataChanged(Data);
            }

            if (MyAPIGateway.Multiplayer.IsServer || (MyAPIGateway.Session != null && ModBlock.ControllerInfo != null && ModBlock.ControllerInfo.ControllingIdentityId == MyAPIGateway.Session.Player.IdentityId))
            {
                //msElapsed = 16;
                sElapsed = 0.01667d;
                if (isSetup && Data.IsActive)
                {
                    if (ModBlock.CubeGrid.Physics != null && !isDocked)
                        RunUprightControl();
                }
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            if (ModBlock == null) return;
            if (!IsReal()) return;

            if (ShouldServerUpdate())
            { //Only run the full zeppelin update if I'm a server!
              // put the stuff you expect to update each frame here
              // msElapsed = 166;
                sElapsed = 0.1667d;

                if (isSetup && Data.IsActive)
                {
                    lcdText = "";

                    if (ModBlock.CustomData != loadedConfig)
                    {
                        ParseConfig(ModBlock.CustomData, "Altitude", "Pitch", "Roll");
                        loadedConfig = ModBlock.CustomData;
                    }

                    RunZeppelin();

                    lcdUpdateCounter -= sElapsed;

                    if (lcdUpdateCounter <= 0)
                    {
                        lcdUpdateCounter = lcdUpdateDelay;

                        UpdateLCD();
                    }
                }
            }
        }

        public void RunZeppelin()
        { //The guts of the controller are implemented here.

            //first check if the zeppelin is docked...
            bool justDocked = false;
            double physicalMass = (double)ModBlock.CalculateShipMass().PhysicalMass;
            double baseMass = (double)ModBlock.CalculateShipMass().BaseMass;

            GetAltitude();
            double surfAltitude = GetSurfaceAltitude();

            //if physicalMass is less than baseMass, then it's likely the grid is locked to voxel or docked to station.
            if (physicalMass < baseMass || ModBlock.CubeGrid.Physics.IsStatic)
            {
                for (int i = 0; i < gears.Count; i++)
                {
                    IMyLandingGear gear = gears[i];
                    if (IsBlockDamaged(gear))
                    {
                        gears.Remove(gear); //oh god concurrent modification of gears list
                        i--;
                        continue;
                    }

                    if (gear.IsLocked)
                    {
                        justDocked = true;
                        break;
                    }
                }

                for (int i = 0; i < connectors.Count; i++)
                {
                    IMyShipConnector connector = connectors[i];
                    if (IsBlockDamaged(connector))
                    {
                        connectors.Remove(connector); //oh god concurrent modification of connectors list
                        i--;
                        continue;
                    }

                    if (connector.IsLocked || connector.IsConnected)
                    {
                        justDocked = true;
                        break;
                    }
                }
            }

            //display heading
            string[] headList = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW", "N" };
            double heading = GetHeading();
            int headIndex = (int)Math.Round(heading / 22.5d);

            lcdText += "Heading: " + (headList[headIndex]) + " ( " + Math.Round(heading) + "° ) \n";

            //lcdText += "Target Altitude: " + Math.RoundData.TargetAltitude- 0.01,3) + "km \n";
            lcdText += "Target Altitude: " + Math.Round(Data.TargetAltitude, 3) + "km \n";
            lcdText += "Current Altitude: " + Math.Round(currentAltitude, 3) + "km \n";
            lcdText += "Surface Altitude: " + Math.Round(surfAltitude) + "m \n";
            lcdText += "Vert Speed: " + Math.Round(GetVerticalVelocity(), 2) + "m/s \n";
            lcdText += "\n";

            if (!justDocked)
            {
                if (ShouldServerUpdate())
                    RunAltitudeControl();


                if (ModBlock.CubeGrid.Physics != null && ShouldRunUpdates())
                    RunUprightControl();
            }
            else
            {
                RunDockedControl();
            }

            if (dockFilled)
            {
                lcdText += "Gas cells at safe levels. \n";
            }


            isDocked = justDocked;
        }

        private void RunAltitudeControl()
        {
            double physicalMass = (double)ModBlock.CalculateShipMass().PhysicalMass;
            double baseMass = (double)ModBlock.CalculateShipMass().BaseMass;

            if (dockFilled)
            {
                SetOnOff(balloons, true);
                dockFilled = false;
            }

            //if the zeppelin is not docked, run controller as usual
            double error = Data.TargetAltitude - currentAltitude;
            double filledRatio = GetFilledRatio(balloons);
            double tankRatio = GetFilledRatio(ballasts);

            //run altitude controller
            //altitude controller here combines both feedforward and feedback control to determine the desired fill ratio
            //Feedforward control is very accurate but yields a pretty slow system response. 
            //Feedback control makes the settle time much faster by overfilling cells or underfilling to get faster acceleration. Feedback also allows for disturbance compensation.

            double feedForward = GetNeededFilledRatio(physicalMass, Data.TargetAltitude);
            double feedBack = AltitudeController.ControllerResponse(Math.Max(-0.05, Math.Min(0.05, error)), sElapsed);

            feedBack = Math.Min(Math.Max(feedBack, -feedBackThreshhold), feedBackThreshhold);

            double controllerOutput = feedForward + feedBack;

            lcdText += "Target Fill: " + Math.Round(controllerOutput, 3) * 100 + "% \n";
            lcdText += "Balloon Fill: " + Math.Round(filledRatio, 3) * 100 + "% \n";
            lcdText += "Ballast Fill: " + Math.Round(tankRatio, 3) * 100 + "% \n";


            double deviation = Math.Abs(controllerOutput - filledRatio); //filled ratio error

            //Apply the controller output.
            if (filledRatio < controllerOutput && deviation > ERROR_MARGIN)
            {
                //increase ratio
                lcdText += "Filling Balloon... \n";

                ToggleExhaust(exhaust, false);
                ToggleGasStockpile(balloons, true);
                ToggleGasStockpile(ballasts, false);

            }
            else if (filledRatio > controllerOutput && deviation > ERROR_MARGIN)
            {
                lcdText += "Emptying Balloon... \n";
                //decrease ratio

                ToggleExhaust(exhaust, false);

                ToggleGasStockpile(balloons, false);
                ToggleGasStockpile(ballasts, true);

                //if the tanks are at capacity, start dumping hydrogen to sink.
                if (tankRatio > maxTankRatio)
                {
                    ToggleGasStockpile(ballasts, false);
                    ToggleExhaust(exhaust, true);
                }
                else
                {
                    ToggleExhaust(exhaust, false);
                }
            }
            else
            {
                lcdText += "Maintaining Balloon... \n";
                //maintain ratio

                ToggleExhaust(exhaust, false);
                ToggleGasStockpile(balloons, false);
                ToggleGasStockpile(ballasts, false);
            }

            //toggle the oxygen farms if the hydrogen tanks are not filled enough. 
            if (tankRatio <= minTankRatio)
            {
                ToggleOxygen(oxygenFarms, true);
            }
            else
            {
                ToggleOxygen(oxygenFarms, false);
            }

            if (printDebug)
            {
                double computedForce = EstimateBalloonForce(filledRatio, 10.0 / 60.0); //(physicalMass * gravity) / (filledRatio * balloons.Count * GetAtmosphericDensity(currentAltitude));
                lcdText += "Computed Balloon Force: \n  " + computedForce + "\n";
            }

        }

        private void RunUprightControl()
        {

            double physicalMass = (double)ModBlock.CalculateShipMass().PhysicalMass;
            double baseMass = (double)ModBlock.CalculateShipMass().BaseMass;

            if (isDocked) return;

            //PID control for pitch and roll
            //find the error for pitch and roll
            double pitchError = 0;
            double rollError = 0;

            Vector3D gravVec = -ModBlock.GetNaturalGravity();

            Vector3D forward = ModBlock.WorldMatrix.Forward;
            Vector3D right = ModBlock.WorldMatrix.Right;
            Vector3D up = ModBlock.WorldMatrix.Up;

            const double quarterCycle = Math.PI / 2;

            pitchError = VectorAngleBetween(forward, gravVec) - quarterCycle;
            rollError = VectorAngleBetween(right, gravVec) - quarterCycle;

            //run the PID control
            double pitchAccel = PitchController.ControllerResponse(pitchError, sElapsed);
            double rollAccel = RollController.ControllerResponse(-rollError, sElapsed);

            //apply angular acceelrations here
            Vector3D angularVel = ModBlock.CubeGrid.Physics.AngularVelocity;
            angularVel += ModBlock.WorldMatrix.Right * pitchAccel;
            angularVel += ModBlock.WorldMatrix.Forward * rollAccel;

            ModBlock.CubeGrid.Physics.AngularVelocity = angularVel;

            ModBlock.CubeGrid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE, null, ModBlock.CubeGrid.Physics.CenterOfMassWorld, angularVel);
        }

        private void RunDockedControl()
        {

            double physicalMass = (double)ModBlock.CalculateShipMass().PhysicalMass;
            double baseMass = (double)ModBlock.CalculateShipMass().BaseMass;

            //if the zeppelin is docked, reset altitude, shut off cells/tanks/vents/generators, and sleep.

            lcdText += "Docked...\n";

            ResetTargetElevation();

            //reset PID states
            ResetPIDState();


            double feedForward = GetNeededFilledRatio(baseMass, Data.TargetAltitude);
            double filledRatio = GetFilledRatio(balloons);
            double tankRatio = GetFilledRatio(ballasts);

            double deviation = Math.Abs(feedForward - filledRatio); //filled ratio error


            if (!dockFilled)
            {
                lcdText += "Target Fill: " + Math.Round(feedForward, 3) * 100 + "% \n";
                lcdText += "Balloon Fill: " + Math.Round(filledRatio, 3) * 100 + "% \n";
                lcdText += "Ballast Fill: " + Math.Round(tankRatio, 3) * 100 + "% \n";

                if (filledRatio < feedForward && deviation > ERROR_MARGIN * 2)
                {
                    //increase ratio
                    lcdText += "Filling Balloon... \n";

                    ToggleExhaust(exhaust, false);
                    ToggleGasStockpile(balloons, true);
                    ToggleGasStockpile(ballasts, false);

                }
                else if (filledRatio > feedForward && deviation > ERROR_MARGIN * 2)
                {
                    lcdText += "Emptying Balloon... \n";
                    //decrease ratio

                    ToggleExhaust(exhaust, false);

                    ToggleGasStockpile(balloons, false);
                    ToggleGasStockpile(ballasts, true);

                    //if the tanks are at capacity, start dumping hydrogen to sink.
                    if (tankRatio > maxTankRatio)
                    {
                        ToggleGasStockpile(ballasts, false);
                        ToggleExhaust(exhaust, true);
                    }
                    else
                    {
                        ToggleExhaust(exhaust, false);
                    }
                }
                else
                {
                    lcdText += "Maintaining Balloon... \n";
                    //maintain ratio

                    ToggleExhaust(exhaust, false);
                    ToggleGasStockpile(balloons, false);
                    ToggleGasStockpile(ballasts, false);

                    SetOnOff(balloons, false);

                    dockFilled = true;
                }
            }
        }

        private double EstimateBalloonForce(double balloonFill, double deltaTime)
        {

            double atmoDensity = 0;
            Vector3D pos = ModBlock.GetPosition();

            HashSet<IMyEntity> planets = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(planets, p => p is MyPlanet);


            foreach (MyPlanet plan in planets)
            {
                if (plan.HasAtmosphere)
                {
                    atmoDensity += plan.GetAirDensity(pos);
                }

            }

            double physicalMass = (double)ModBlock.CalculateShipMass().PhysicalMass;
            double baseMass = (double)ModBlock.CalculateShipMass().BaseMass;

            double upVel = GetVerticalVelocity();
            double upAccel = upVel - lastUpwardsVelocity;
            double upForce = (upAccel + gravity) * physicalMass / (balloons.Count * balloonFill * atmoDensity);

            lastUpwardsVelocity = upVel;
            return upForce;
        }

        private bool IsBlockDamaged(IMyTerminalBlock block)
        {
            if (block.CubeGrid.GetCubeBlock(block.Position) == null) return true;

            IMySlimBlock slim = block.CubeGrid.GetCubeBlock(block.Position) as IMySlimBlock;
            if (slim.IsDestroyed) return true;

            if (slim.CurrentDamage / slim.BuildIntegrity > 0.9)
                return true;

            return false;
        }

        private double GetAltitude()
        {
            if (ModBlock == null || !IsReal()) return 0.0;

            ModBlock.TryGetPlanetElevation(Sandbox.ModAPI.Ingame.MyPlanetElevation.Sealevel, out currentAltitude);
            currentAltitude /= 1000;
            return currentAltitude;
        }

        private double GetSurfaceAltitude()
        {
            if (ModBlock == null || !IsReal()) return 0.0;

            double surfaceAltitude = 0;
            ModBlock.TryGetPlanetElevation(Sandbox.ModAPI.Ingame.MyPlanetElevation.Surface, out surfaceAltitude);
            return surfaceAltitude;
        }

        private double GetHeading()
        {
            if (ModBlock == null || !IsReal()) return 0.0;

            double heading = 0;
            double heading2 = 0;

            Vector3D north = new Vector3D(0, -1, 0); //WHY IS THIS NORTH!?

            Vector3D nr = north.Cross(ModBlock.WorldMatrix.Up);

            if (nr.X == 0 && nr.Y == 0 && nr.Z == 0) return 0.0; //This can only happen when I'm directly at the north pole.

            heading = VectorAngleBetween(nr, ModBlock.WorldMatrix.Right) * 180 / Math.PI;

            if (ModBlock.WorldMatrix.Forward.Dot(nr) < 0)
            {
                //western quadrants!
                heading = 360.0 - heading;
            }

            return heading;
        }

        public void ChangeTargetElevation(float value)
        {
            Data.TargetAltitude += value;
            Core.SendDataChanged(Data);
        }

        public void ResetTargetElevation()
        {
            Data.TargetAltitude = (float)GetAltitude();
            Core.SendDataChanged(Data);
        }

        private double GetNeededFilledRatio(double shipMass, double desiredAltitude)
        {
            double ratio = (shipMass * gravity) / (balloons.Count * balloonForce * GetAtmosphericDensity(desiredAltitude));
            return ratio;
        }

        private void ResetPIDState()
        {
            AltitudeController.Reset();
            PitchController.Reset();
            RollController.Reset();
        }

        private void UpdateLCD()
        {
            if (!ShouldServerUpdate()) return;
            if (lcd == null) return;
            if (!hasLCD) return;

            lcd.Enabled = true;
            lcd.ShowPublicTextOnScreen();
            lcd.WritePublicText(lcdText, false);
        }

        public void ToggleActive()
        {
            Data.IsActive = !Data.IsActive;
            Core.SendDataChanged(Data);

            WriteNewConfig();
        }

        public void ActiveHotbarText(IMyTerminalBlock cockpit, System.Text.StringBuilder hotbarText)
        {
            hotbarText.Clear();
            hotbarText.Append(Data.IsActive ? "Zepp On" : "Zepp Off");
        }

        private void WriteNewConfig()
        {
            if (!ShouldServerUpdate()) return;

            ModBlock.CustomData = "";
            ModBlock.CustomData += WriteConfig();
            ModBlock.CustomData += "\n";
            ModBlock.CustomData += WriteConfigPID(AltitudeController, "Altitude PID ");
            // ModBlock.CustomData += WriteConfigPID(PitchController, "Pitch PID ");
            // ModBlock.CustomData += WriteConfigPID(RollController, "Roll PID ");

            loadedConfig = ModBlock.CustomData;
        }

        private string WriteConfigPID(PID control, string name)
        {
            string text = "";
            text += "" + name + "kP = " + control.kP + "\n";
            text += "" + name + "kI = " + control.kI + "\n";
            text += "" + name + "kD = " + control.kD + "\n";
            //text += "" + name + "Decay = " + control.integralDecay + "\n";

            return text;
        }

        private string WriteConfig()
        {
            string text = "";
            text += "Text LCD must have \"" + LCDname + "\" in the public title. \n";
            text += "H2 exhaust must be named \"" + exhaustName + "\" \n";
            text += "H2 ballast tanks must be named \"" + ballastName + "\" \n";
            text += "Main cockpit must be marked. \n";
            text += "\n";
            text += "" + "Activate Controller = " + Data.IsActive + "\n";
            text += "\n";
            text += "" + "Use Exhaust = " + useExhaust + "\n";
            text += "" + "Use H2 Farm = " + useGenerator + "\n";

            return text;
        }

        private void ParseConfig(string text, string altName, string pitchName, string rollName)
        {
            if (!ShouldServerUpdate()) return;

            string[] lines = text.Split('\n');

            foreach (string line in lines)
            {
                if (!line.Contains("=")) continue;
                string[] split = line.Split('=');
                split[0].Trim();
                split[1].Trim();

                if (split[0].Contains("Use Exhaust"))
                {
                    useExhaust = Convert.ToBoolean(split[1]);
                    continue;
                }
                if (split[0].Contains("Use H2 Farm"))
                {
                    useGenerator = Convert.ToBoolean(split[1]);
                    continue;
                }
                if (split[0].Contains("Activate Controller"))
                {
                    Data.IsActive = Convert.ToBoolean(split[1]);
                    continue;
                }

                if (split[0].Contains("Debug"))
                {
                    printDebug = Convert.ToBoolean(split[1]);
                    continue;
                }

                PID control = null;
                if (split[0].Contains(altName)) control = AltitudeController;
                //if (split[0].Contains(pitchName)) control = PitchController;
                //if (split[0].Contains(rollName)) control = RollController;

                if (control == null) continue;

                if (split[0].Contains("kP")) control.kP = Convert.ToDouble(split[1]);
                if (split[0].Contains("kI")) control.kI = Convert.ToDouble(split[1]);
                if (split[0].Contains("kD")) control.kD = Convert.ToDouble(split[1]);
                if (split[0].Contains("Decay")) control.integralDecay = Convert.ToDouble(split[1]);
            }

            loadedConfig = text;
        }

        private double GetAtmosphericDensity(double altitudeKM)
        {
            double eff = -0.0712151286 * altitudeKM + 0.999714809;
            if (eff > 1)
                eff = 1;

            return eff;
        }

        private void ToggleOxygen(List<IMyOxygenFarm> oxy, bool on)
        {
            for (int i = 0; i < oxy.Count; i++)
            {
                IMyOxygenFarm farm = oxy[i];
                if (IsBlockDamaged(farm))
                {
                    oxy.Remove(farm);
                    i--;
                    continue;
                }

                //uhhh idk what to put here to turn it on or off.....
            }
        }

        private void ToggleGasStockpile(List<IMyGasTank> tanks, bool on)
        {

            for (int i = 0; i < tanks.Count; i++)
            {
                IMyGasTank tank = tanks[i];
                if (IsBlockDamaged(tank))
                {
                    tanks.Remove(tank);
                    i--;
                    continue;
                }

                tank.Stockpile = on;
                tank.Enabled = true;
            }
        }

        private void ToggleExhaust(List<IMyThrust> thrust, bool on)
        {
            if (!useExhaust) return;
            if (!hasExhaust) return;

            for (int i = 0; i < thrust.Count; i++)
            {
                IMyThrust thruster = thrust[i];
                if (IsBlockDamaged(thruster))
                {
                    thrust.Remove(thruster);
                    i--;
                    continue;
                }

                thruster.Enabled = on;
                thruster.ThrustOverride = 100;
            }
        }

        private double GetFilledRatio(List<IMyGasTank> tanks)
        {
            double total = 0;

            for (int i = 0; i < tanks.Count; i++)
            {
                IMyGasTank tank = tanks[i];
                if (IsBlockDamaged(tank))
                {
                    tanks.Remove(tank);
                    i--;
                    continue;
                }
                total += tank.FilledRatio;
            }

            total /= tanks.Count;
            return total;
        }

        private void SetOnOff(List<IMyGasTank> list, Boolean onOff)
        {
            foreach (IMyGasTank e in list)
            {
                e.Enabled = onOff;
            }
        }

        private double VectorAngleBetween(Vector3D a, Vector3D b)
        { //returns radians
          //Law of cosines to return the angle between two vectors.

            if (a.LengthSquared() == 0 || b.LengthSquared() == 0)
                return 0;
            else
                return Math.Acos(MathHelper.Clamp(a.Dot(b) / a.Length() / b.Length(), -1, 1));
        }

        private double GetVerticalVelocity()
        {
            Vector3D gravVec = ModBlock.GetNaturalGravity();
            gravVec.Normalize();
            return -ModBlock.GetShipVelocities().LinearVelocity.Dot(gravVec);
        }

        private Vector3D VectorProjection(Vector3D a, Vector3D b) //component of a parallel to b
        {
            if (b.LengthSquared() == 0 || a.LengthSquared() == 0) return Vector3D.Zero;
            Vector3D projection = a.Dot(b) / b.LengthSquared() * b;
            return projection;
        }

        private Vector3D VectorRejection(Vector3D a, Vector3D b) //component of a perpendicular to b
        {
            return a - VectorProjection(a, b);
        }

        private bool ShouldRunUpdates()
        {
            return ShouldServerUpdate() || ShouldClientUpdate();
        }

        private bool ShouldServerUpdate()
        {
            //run updates if
            // - I'm a dedicated server
            // - I'm in singleplayer
            // - I'm not a server BUT I am a pilot
            // - I'm hosting a server through the client

            return MyAPIGateway.Multiplayer.IsServer ||
                MyAPIGateway.Multiplayer.MultiplayerActive == false || MyAPIGateway.Session.MultiplayerAlive == false;
        }

        private bool ShouldClientUpdate()
        {
            IMyEntity chara = (MyAPIGateway.Session.LocalHumanPlayer?.Character?.ControllerInfo?.Controller?.ControlledEntity?.Entity);
            //for some reason, MyAPIGateway.Session.LocalHumanPlayer?.Character?.ControllerInfo?.Controller?.ControlledEntity?.Entity is set to the suit when I'm out of cockpit, and it's set to null when I'm in a cockpit

            return (!MyAPIGateway.Multiplayer.IsServer && (chara == null || !(chara is IMyCharacter))) || ModBlock.Pilot == MyAPIGateway.Session?.LocalHumanPlayer?.Character;
        }

        private bool IsReal()
        {
            int flags = (int)(ModBlock.Flags & EntityFlags.Transparent);
            if (flags != 0) return false;

            if (ModBlock.SlimBlock.Dithering != 1 && ModBlock.SlimBlock.Dithering != 0) return false;

            //if (ModBlock.Physics == null) return false;

            if (ModBlock.SlimBlock.BuildLevelRatio == 0) return false;
            if (((MyCubeGrid)ModBlock.CubeGrid).Projector != null) return false;

            return true;
        }

        private void DebugPrint(string s)
        {
            MyAPIGateway.Utilities.ShowNotification(s, 1);
        }
    }
}
