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
using System.Text;

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

        // We should reduce this to something less
        private bool IsRealGrid => (((MyCubeGrid)ModBlock.CubeGrid).Projector == null) &&
                                    (ModBlock.Flags & EntityFlags.Transparent) == 0;// &&
                                    //(ModBlock.SlimBlock.Dithering == 1 || ModBlock.SlimBlock.Dithering == 0) &&
                                    //(ModBlock.SlimBlock.BuildLevelRatio != 0);


        private IMyTerminalControlOnOffSwitch ZeppelinOnOffControl = null;
        private IMyTerminalControlSlider ZeppelinAltitudeControl = null;
        private IMyTerminalControlButton ZeppelinSetupControl = null;
        private IMyTerminalAction Climb1Action = null;
        private IMyTerminalAction Climb10Action = null;
        private IMyTerminalAction Climb100Action = null;
        private IMyTerminalAction Drop1Action = null;
        private IMyTerminalAction Drop10Action = null;
        private IMyTerminalAction Drop100Action = null;
        private IMyTerminalAction SetCurrentAction = null;
        private IMyTerminalAction ZeppelinOnOfAction = null;
        private IMyTerminalAction SetupAction = null;

        private bool ControlsInitialized = false;

        private bool isSetup = false;
        private bool isDocked = false;
        private bool hasExhaust = false;
        private bool hasLCD = false;

        private bool dockFilled = false;

        private bool useExhaust = true;
        private bool useGenerator = false;

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
        private double lastUpwardsVelocity = 0;

        //private const double balloonForce = 755194.1960563; // k = 200 * 1000 / 200;
        private const double balloonForce = 1137782;//1154500;// k = 300 * 1000 / 200;
                                                    //1121064
                                                    //901650

        private const double ERROR_MARGIN = 0.0010;
        private double lcdUpdateCounter = 0.25;
        private double lcdUpdateDelay = 0.5;
        private StringBuilder lcdText = new StringBuilder();
        private string loadedConfig = "";
        private double feedBackThreshhold = 0.35;
        private bool printDebug = false;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            ModBlock = Entity as IMyCockpit;
            Data = new ZeppelinData() { BlockId = Entity.EntityId, TargetAltitude = 3.5f };

            Core.RegisterZeppelin(this);
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.EACH_FRAME; // this is how you add flags to run the functions below
        }

        public override void Close()
        {
            Core.UnregisterZeppelin(this);
            NeedsUpdate = MyEntityUpdateEnum.NONE;
        }

        public override void UpdateOnceBeforeFrame()
        {
            CreateControls();

            if (MyAPIGateway.Multiplayer.IsServer && IsRealGrid && !isSetup)
            {
                ZeppSetup();
                Core.SendDataChanged(Data);
            }
        }

        public override void UpdateBeforeSimulation()
        {
            if (!IsRealGrid) return;

            // turn off zeppelin controller if this is not the main
            if (MyAPIGateway.Multiplayer.IsServer && Data.IsActive && otherCockpits.Count > 0 && !ModBlock.IsMainCockpit)
            {
                Data.IsActive = false;
                Core.SendDataChanged(Data);
            }

            if (MyAPIGateway.Multiplayer.IsServer || (MyAPIGateway.Session != null && ModBlock.ControllerInfo != null && ModBlock.ControllerInfo.ControllingIdentityId == MyAPIGateway.Session.Player.IdentityId))
            {
                sElapsed = 0.01667d;
                if (Data.IsActive && ModBlock.CubeGrid.Physics != null)
                {
                    RunUprightControl();
                }
            }
        }

        /// <summary>
        /// Only run the full zeppelin update if I'm a server!
        /// put the stuff you expect to update each frame here
        /// </summary>
        public override void UpdateBeforeSimulation10()
        {
            if (!MyAPIGateway.Multiplayer.IsServer || !IsRealGrid) return;

            sElapsed = 0.1667d;

            if (isSetup && Data.IsActive)
            {
                lcdText.Clear();

                if (ModBlock.CustomData != loadedConfig)
                {
                    ParseConfig(ModBlock.CustomData, "Altitude", "Pitch", "Roll");
                    loadedConfig = ModBlock.CustomData;
                }

                RunZeppelin();

                if (lcdUpdateCounter <= 0)
                {
                    UpdateLCD();
                    lcdUpdateCounter = lcdUpdateDelay;
                }

                lcdUpdateCounter -= sElapsed;
            }
        }

        public void ZeppSetup()
        {
            isSetup = false;
            hasLCD = false;
            hasExhaust = false;
            loadedConfig = "";

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
                else if (block is IMyCockpit && !block.BlockDefinition.IsNull() && block.BlockDefinition.SubtypeId == "CockpitOpen")
                {
                    otherCockpits.Add(block as IMyCockpit);
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

                Core.RegisterZeppelin(this);
            }
        }

        public void RunZeppelin()
        { //The guts of the controller are implemented here.

            //first check if the zeppelin is docked...
            bool justDocked = false;
            double physicalMass = ModBlock.CalculateShipMass().PhysicalMass;
            double baseMass = ModBlock.CalculateShipMass().BaseMass;

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

            lcdText.Append("Heading: " + (headList[headIndex]) + " ( " + Math.Round(heading) + "° ) \n");

            //lcdText += "Target Altitude: " + Math.RoundData.TargetAltitude- 0.01,3) + "km \n";
            lcdText.Append("Target Altitude: " + Math.Round(Data.TargetAltitude, 3) + "km \n");
            lcdText.Append("Current Altitude: " + Math.Round(currentAltitude, 3) + "km \n");
            lcdText.Append("Surface Altitude: " + Math.Round(surfAltitude) + "m \n");
            lcdText.Append("Vert Speed: " + Math.Round(GetVerticalVelocity(), 2) + "m/s \n");
            lcdText.Append("\n");

            if (!justDocked)
            {
                if (MyAPIGateway.Multiplayer.IsServer)
                {
                    RunAltitudeControl();
                }

                if (ModBlock.CubeGrid.Physics != null)
                {
                    RunUprightControl();
                }
            }
            else
            {
                RunDockedControl();
            }

            if (dockFilled)
            {
                lcdText.Append("Gas cells at safe levels. \n");
            }

            isDocked = justDocked;
        }

        public void UpdateZeppelinData(ZeppelinData data)
        {
            Data.TargetAltitude = data.TargetAltitude;
            Data.IsActive = data.IsActive;

            ZeppelinOnOffControl.UpdateVisual();
            ZeppelinAltitudeControl.UpdateVisual();
            ZeppelinSetupControl.UpdateVisual();

            //ZeppelinOnOffControl.RedrawControl();
            //ZeppelinAltitudeControl.RedrawControl();
            //ZeppelinSetupControl.RedrawControl();

            ZeppelinData dataForOtherZeppelinControllers = new ZeppelinData()
            {
                TargetAltitude = data.TargetAltitude,
                IsActive = false
            };

            foreach (IMyCockpit cockpit in otherCockpits)
            {
                if (cockpit.GameLogic is ZeppelinController)
                {
                    (cockpit.GameLogic as ZeppelinController).UpdateZeppelinData(dataForOtherZeppelinControllers);
                }
            }

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

        public void ToggleActive()
        {
            Data.IsActive = !Data.IsActive;
            Core.SendDataChanged(Data);

            WriteNewConfig();
        }

        public void ActiveHotbarText(IMyTerminalBlock cockpit, StringBuilder hotbarText)
        {
            hotbarText.Clear();
            hotbarText.Append(Data.IsActive ? "Zepp On" : "Zepp Off");
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

            lcdText.Append("Target Fill: " + Math.Round(controllerOutput, 3) * 100 + "% \n");
            lcdText.Append("Balloon Fill: " + Math.Round(filledRatio, 3) * 100 + "% \n");
            lcdText.Append("Ballast Fill: " + Math.Round(tankRatio, 3) * 100 + "% \n");


            double deviation = Math.Abs(controllerOutput - filledRatio); //filled ratio error

            //Apply the controller output.
            if (filledRatio < controllerOutput && deviation > ERROR_MARGIN)
            {
                //increase ratio
                lcdText.Append("Filling Balloon... \n");

                ToggleExhaust(exhaust, false);
                ToggleGasStockpile(balloons, true);
                ToggleGasStockpile(ballasts, false);

            }
            else if (filledRatio > controllerOutput && deviation > ERROR_MARGIN)
            {
                lcdText.Append("Emptying Balloon... \n");
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
                lcdText.Append("Maintaining Balloon... \n");
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
                lcdText.Append("Computed Balloon Force: \n  " + computedForce + "\n");
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

            lcdText.Append("Docked...\n");

            ResetTargetElevation();

            //reset PID states
            ResetPIDState();


            double feedForward = GetNeededFilledRatio(baseMass, Data.TargetAltitude);
            double filledRatio = GetFilledRatio(balloons);
            double tankRatio = GetFilledRatio(ballasts);

            double deviation = Math.Abs(feedForward - filledRatio); //filled ratio error


            if (!dockFilled)
            {
                lcdText.Append("Target Fill: " + Math.Round(feedForward, 3) * 100 + "% \n");
                lcdText.Append("Balloon Fill: " + Math.Round(filledRatio, 3) * 100 + "% \n");
                lcdText.Append("Ballast Fill: " + Math.Round(tankRatio, 3) * 100 + "% \n");

                if (filledRatio < feedForward && deviation > ERROR_MARGIN * 2)
                {
                    //increase ratio
                    lcdText.Append("Filling Balloon... \n");

                    ToggleExhaust(exhaust, false);
                    ToggleGasStockpile(balloons, true);
                    ToggleGasStockpile(ballasts, false);

                }
                else if (filledRatio > feedForward && deviation > ERROR_MARGIN * 2)
                {
                    lcdText.Append("Emptying Balloon... \n");
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
                    lcdText.Append("Maintaining Balloon... \n");
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
            if (ModBlock == null || !IsRealGrid) return 0.0;

            ModBlock.TryGetPlanetElevation(Sandbox.ModAPI.Ingame.MyPlanetElevation.Sealevel, out currentAltitude);
            currentAltitude /= 1000;
            return currentAltitude;
        }

        private double GetSurfaceAltitude()
        {
            if (ModBlock == null || !IsRealGrid) return 0.0;

            double surfaceAltitude = 0;
            ModBlock.TryGetPlanetElevation(Sandbox.ModAPI.Ingame.MyPlanetElevation.Surface, out surfaceAltitude);
            return surfaceAltitude;
        }

        private double GetHeading()
        {
            if (ModBlock == null || !IsRealGrid) return 0.0;

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
            if (!MyAPIGateway.Multiplayer.IsServer) return;
            if (lcd == null) return;
            if (!hasLCD) return;

            lcd.Enabled = true;
            lcd.ShowPublicTextOnScreen();
            lcd.WritePublicText(lcdText, false);
        }

        private void WriteNewConfig()
        {
            if (!MyAPIGateway.Multiplayer.IsServer) return;

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
            if (!MyAPIGateway.Multiplayer.IsServer) return;

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

        private void CreateControls()
        {
            if (ControlsInitialized) return;
            ControlsInitialized = true;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                #region OnOff Toggle

                ZeppelinOnOffControl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyCockpit>("Zeppelin Controller On/Off");
                ZeppelinOnOffControl.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                ZeppelinOnOffControl.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                ZeppelinOnOffControl.Getter = (block) => Data.IsActive;
                ZeppelinOnOffControl.Setter = (block, value) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "toggle_active", DataType = block.EntityId.ToString() });
                    }
                    else
                    {
                        ToggleActive();
                    }
                };
                ZeppelinOnOffControl.Title = MyStringId.GetOrCompute("Zeppelin Controller On/Off");
                ZeppelinOnOffControl.OnText = MyStringId.GetOrCompute("On");
                ZeppelinOnOffControl.OffText = MyStringId.GetOrCompute("Off");
                MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(ZeppelinOnOffControl);

                #endregion

                #region Altitude Slider

                ZeppelinAltitudeControl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyCockpit>("Zeppelin Altitude");
                ZeppelinAltitudeControl.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                ZeppelinAltitudeControl.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                ZeppelinAltitudeControl.Setter = (block, value) =>
                {
                    Data.TargetAltitude = value;
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendDataChanged(Data);
                    }
                };
                ZeppelinAltitudeControl.Getter = (block) => Data.TargetAltitude;
                ZeppelinAltitudeControl.Writer = (block, value) => value.Append($"{Data.TargetAltitude.ToString("n3")} km");
                ZeppelinAltitudeControl.Title = MyStringId.GetOrCompute("Zeppelin Altitude");
                ZeppelinAltitudeControl.Tooltip = MyStringId.GetOrCompute("km Distance above sea level");
                ZeppelinAltitudeControl.SetLimits(0, 20);
                MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(ZeppelinAltitudeControl);

                #endregion

                #region Setup Button

                ZeppelinSetupControl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyCockpit>("Update Zeppelin Setup");
                ZeppelinSetupControl.Visible = (block) => { return block.EntityId == ModBlock.EntityId; };
                ZeppelinSetupControl.Enabled = (block) => { return block.EntityId == ModBlock.EntityId; };
                ZeppelinSetupControl.Title = MyStringId.GetOrCompute("Update Zeppelin Setup");
                ZeppelinSetupControl.Tooltip = MyStringId.GetOrCompute("Use to update controller blocks & config");
                ZeppelinSetupControl.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "setup", DataType = b.EntityId.ToString() });
                    }
                    else
                    {
                        ZeppSetup();
                    }
                };
                MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(ZeppelinSetupControl);

                #endregion

                #region Action Climb 1m

                Climb1Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Climb 1m");
                Climb1Action.Name.Append($"Zeppelin Climb 1m");
                Climb1Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                Climb1Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Climb1Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Climb1Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = UP_1.ToString() });
                    }
                    else
                    {
                        ChangeTargetElevation(UP_1);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Climb1Action);

                #endregion

                #region Action Drop 1m

                Drop1Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Drop 1m");
                Drop1Action.Name.Append($"Zeppelin Drop 1m");
                Drop1Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                Drop1Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Drop1Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Drop1Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = DOWN_1.ToString() });
                    }
                    else
                    {
                        ChangeTargetElevation(DOWN_1);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Drop1Action);

                #endregion

                #region Action Climb 10m

                Climb10Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Climb 10m");
                Climb10Action.Name.Append($"Zeppelin Climb 10m");
                Climb10Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                Climb10Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Climb10Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Climb10Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = UP_10.ToString() });
                    }
                    else
                    {
                        ChangeTargetElevation(UP_10);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Climb10Action);

                #endregion

                #region Action Drop 10m

                Drop10Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Drop 10m");
                Drop10Action.Name.Append($"Zeppelin Drop 10m");
                Drop10Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                Drop10Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Drop10Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Drop10Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = DOWN_10.ToString() });
                    }
                    else
                    {
                        ChangeTargetElevation(DOWN_10);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Drop10Action);

                #endregion

                #region Action Climb 100m

                Climb100Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Climb 100m");
                Climb100Action.Name.Append($"Zeppelin Climb 100m");
                Climb100Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                Climb100Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Climb100Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Climb100Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = UP_100.ToString() });
                    }
                    else
                    {
                        ChangeTargetElevation(UP_100);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Climb100Action);

                #endregion

                #region Action Drop 100m

                Drop100Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Zeppelin Drop 100m");
                Drop100Action.Name.Append($"Zeppelin Drop 100m");
                Drop100Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                Drop100Action.Writer = (b, str) => str.Append($"{Data.TargetAltitude.ToString("n3")}km");
                Drop100Action.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                Drop100Action.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "change", DataType = b.EntityId.ToString(), XMLData = DOWN_100.ToString() });
                    }
                    else
                    {
                        ChangeTargetElevation(DOWN_100);
                    }
                };

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Drop100Action);

                #endregion

                #region Action Set Current Altitude

                SetCurrentAction = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>($"Set Current Altitude");
                SetCurrentAction.Name.Append($"Set Current Altitude");
                SetCurrentAction.Writer = (b, str) => str.Append($"Set Current Altitude");
                SetCurrentAction.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                SetCurrentAction.Action = (b) =>
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

                MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(SetCurrentAction);

                #endregion

                #region Action OnOff Controller

                ZeppelinOnOfAction = MyAPIGateway.TerminalControls.CreateAction<Sandbox.ModAPI.Ingame.IMyCockpit>("ZeppelinControllerOn/Off");
                ZeppelinOnOfAction.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                ZeppelinOnOfAction.Action = (b) =>
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
                ZeppelinOnOfAction.Name = new StringBuilder("Zeppelin Controller On/Off");
                ZeppelinOnOfAction.Writer = ActiveHotbarText;
                MyAPIGateway.TerminalControls.AddAction<Sandbox.ModAPI.Ingame.IMyCockpit>(ZeppelinOnOfAction);

                #endregion

                #region Action Setup Zeppelin Controller

                SetupAction = MyAPIGateway.TerminalControls.CreateAction<Sandbox.ModAPI.Ingame.IMyCockpit>("SetupZeppelinController");
                SetupAction.Enabled = (b) => { return b.EntityId == ModBlock.EntityId; };
                SetupAction.Action = (b) =>
                {
                    if (!MyAPIGateway.Multiplayer.IsServer)
                    {
                        Core.SendRequest(new Coms.Command() { Arguments = "setup", DataType = b.EntityId.ToString() });
                    }
                    else
                    {
                        ZeppSetup();
                    }
                };

                SetupAction.Name = new StringBuilder("Run Setup");
                SetupAction.Writer = (b, sb) => sb.Append("Run Setup");
                MyAPIGateway.TerminalControls.AddAction<Sandbox.ModAPI.Ingame.IMyCockpit>(SetupAction);

                #endregion

            }
        }
    }
}
