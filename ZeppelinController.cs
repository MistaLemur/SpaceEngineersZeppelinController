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
using ModNetworkAPI;

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
        private bool IsRealGrid => ModBlock.CubeGrid.Physics != null;

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
        private IMyTerminalAction ZeppelinOnOffAction = null;
        private IMyTerminalAction SetupAction = null;

        private bool FirstTimeSyncToPlayer = true;

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
        private List<IMyGyro> gyros = new List<IMyGyro>();

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

        private NetworkAPI Network => NetworkAPI.Instance;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            ModBlock = Entity as IMyCockpit;
            Data = new ZeppelinData() { BlockId = Entity.EntityId, TargetAltitude = 3.5f };

            Core.RegisterZeppelin(this);
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.EACH_FRAME; // this is how you add flags to run the functions below
        }

        public override void Close()
        {
            TurnOffZeppelinControl();
            ToggleGyroOnOff(false);

            Core.UnregisterZeppelin(this);
            NeedsUpdate = MyEntityUpdateEnum.NONE;
        }

        public override void UpdateOnceBeforeFrame()
        {
            CreateControls();

            if (MyAPIGateway.Multiplayer.IsServer && IsRealGrid && !isSetup)
            {
                ZeppSetup();

                Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(Data));
            }
        }

        public override void UpdateBeforeSimulation()
        {
            if (!IsRealGrid) return;

            // turn off zeppelin controller if this is not the main
            if (MyAPIGateway.Multiplayer.IsServer && Data.IsActive && otherCockpits.Count > 0 && !ModBlock.IsMainCockpit)
            {
                Data.IsActive = false;

                Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(Data));
            }

            if (MyAPIGateway.Multiplayer.IsServer || (MyAPIGateway.Session != null && ModBlock.ControllerInfo != null && ModBlock.ControllerInfo.ControllingIdentityId == MyAPIGateway.Session.Player.IdentityId))
            {
                if (FirstTimeSyncToPlayer)
                {
                    Network.SendCommand("sync", null, MyAPIGateway.Utilities.SerializeToBinary(ModBlock.EntityId));

                    FirstTimeSyncToPlayer = false;
                }

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

            if (isSetup)
            {
                ToggleGyroOnOff(Data.IsActive);
            }

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
            gyros.Clear();

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
                else if (block is IMyGyro)
                {
                    gyros.Add(block as IMyGyro);
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
                ToggleGyroOnOff(Data.IsActive);
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

            ZeppelinData dataForOtherZeppelinControllers = new ZeppelinData()
            {
                TargetAltitude = data.TargetAltitude,
                IsActive = false
            };

            foreach (IMyCockpit cockpit in otherCockpits)
            {
                ZeppelinController controller = cockpit.GameLogic as ZeppelinController;

                if (controller != null)
                {
                    controller.UpdateZeppelinData(dataForOtherZeppelinControllers);
                }
            }

        }

        public void ChangeTargetElevation(float value)
        {
            Data.TargetAltitude += value;

            Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(Data));
        }

        public void ResetTargetElevation()
        {
            Data.TargetAltitude = (float)GetAltitude();

            Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(Data));
        }

        public void ToggleActive()
        {
            Data.IsActive = !Data.IsActive;
            if (!Data.IsActive) TurnOffZeppelinControl();
            ToggleGyroOnOff(Data.IsActive);

            Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(Data));

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
                    SetOnOff(balloons, true);
                    //increase ratio
                    lcdText.Append("Filling Balloon... \n");

                    ToggleExhaust(exhaust, false);
                    ToggleGasStockpile(balloons, true);
                    ToggleGasStockpile(ballasts, false);

                }
                else if (filledRatio > feedForward && deviation > ERROR_MARGIN * 2)
                {
                    SetOnOff(balloons, true);
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

        private void TurnOffZeppelinControl()
        {
            //this function is called when a zeppelin controller is disabled.
            //This sets all ballasts and all balloons to neutral position
            foreach (IMyGasTank balloon in balloons)
            {
                if (IsBlockDamaged(balloon)) continue;
                balloon.Stockpile = false;
                balloon.Enabled = true;
            }

            foreach (IMyGasTank ballast in ballasts)
            {
                if (IsBlockDamaged(ballast)) continue;
                ballast.Stockpile = false;
                ballast.Enabled = true;
            }
        }

        private void ToggleGyroOnOff(bool onoff)
        {
            //This function turns all gyros on grid on or off.
            foreach (IMyGyro gyro in gyros)
            {
                gyro.Enabled = onoff;
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
                    ToggleGyroOnOff(Data.IsActive);

                    if (!Data.IsActive) TurnOffZeppelinControl();

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


        private const string ID_ZepOnOff = "Zeppelin Controller On/Off";
        private const string ID_ZepSetup = "Zeppelin Setup";
        private const string ID_ZepAltitude = "Zeppelin Altitude";
        private const string ID_SetCurrentAltitude = "Set Current Altitude";

        private const string ID_ZepClimb1 = "Zeppelin Climb 1m";
        private const string ID_ZepClimb10 = "Zeppelin Climb 10m";
        private const string ID_ZepClimb100 = "Zeppelin Climb 100m";
        private const string ID_ZepDrop1 = "Zeppelin Drop 1m";
        private const string ID_ZepDrop10 = "Zeppelin Drop 10m";
        private const string ID_ZepDrop100 = "Zeppelin Drop 100m";

        private void CreateControls()
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                List<IMyTerminalControl> controls;
                MyAPIGateway.TerminalControls.GetControls<IMyCockpit>(out controls);

                List<IMyTerminalAction> actions;
                MyAPIGateway.TerminalControls.GetActions<IMyCockpit>(out actions);

                #region OnOff Toggle

                if (!controls.Exists(x => x.Id == ID_ZepOnOff))
                {
                    ZeppelinOnOffControl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyCockpit>(ID_ZepOnOff);
                    ZeppelinOnOffControl.Visible = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    ZeppelinOnOffControl.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };

                    ZeppelinOnOffControl.Getter = (block) =>
                    {
                        ZeppelinController zep = block.GameLogic.GetAs<ZeppelinController>();

                        if (zep == null) return false;

                        return zep.Data.IsActive;

                    };

                    ZeppelinOnOffControl.Setter = (block, value) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            Network.SendCommand("toggle_active", null, MyAPIGateway.Utilities.SerializeToBinary(block.EntityId));
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
                }

                #endregion

                #region Altitude Slider

                if (!controls.Exists(x => x.Id == ID_ZepAltitude))
                {
                    ZeppelinAltitudeControl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyCockpit>(ID_ZepAltitude);
                    ZeppelinAltitudeControl.Visible = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    ZeppelinAltitudeControl.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };

                    ZeppelinAltitudeControl.Setter = (block, value) =>
                    {
                        ZeppelinController zep = block.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            zep.Data.TargetAltitude = value;

                            if (!MyAPIGateway.Multiplayer.IsServer)
                            {
                                Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(zep.Data));
                            }
                        }
                    };

                    ZeppelinAltitudeControl.Getter = (block) =>
                    {
                        ZeppelinController zep = block.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            return zep.Data.TargetAltitude;
                        }

                        return 0;
                    };

                    ZeppelinAltitudeControl.Writer = (block, value) =>
                    {
                        ZeppelinController zep = block.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            value.Append($"{zep.Data.TargetAltitude.ToString("n3")} km");
                        }
                    };

                    ZeppelinAltitudeControl.Title = MyStringId.GetOrCompute("Zeppelin Altitude");
                    ZeppelinAltitudeControl.Tooltip = MyStringId.GetOrCompute("km Distance above sea level");
                    ZeppelinAltitudeControl.SetLimits(0, 20);
                    MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(ZeppelinAltitudeControl);
                }

                #endregion

                #region Setup Button

                if (!controls.Exists(x => x.Id == ID_ZepSetup))
                {
                    ZeppelinSetupControl = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyCockpit>(ID_ZepSetup);
                    ZeppelinSetupControl.Visible = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    ZeppelinSetupControl.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    ZeppelinSetupControl.Title = MyStringId.GetOrCompute("Update Zeppelin Setup");
                    ZeppelinSetupControl.Tooltip = MyStringId.GetOrCompute("Use to update controller blocks & config");
                    ZeppelinSetupControl.Action = (block) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            Network.SendCommand("setup", null, MyAPIGateway.Utilities.SerializeToBinary(block.EntityId));
                        }
                        else
                        {
                            ZeppSetup();
                        }
                    };

                    MyAPIGateway.TerminalControls.AddControl<IMyCockpit>(ZeppelinSetupControl);
                }

                #endregion

                #region Action Climb 1m

                if (!actions.Exists(x => x.Id == ID_ZepClimb1))
                {
                    Climb1Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_ZepClimb1);
                    Climb1Action.Name.Append(ID_ZepClimb1);
                    Climb1Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                    Climb1Action.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    Climb1Action.Writer = (b, str) =>
                    {
                        ZeppelinController zep = b.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            str.Append($"{zep.Data.TargetAltitude.ToString("n3")}km");
                        }
                    };

                    Climb1Action.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = UP_1 };
                            Network.SendCommand("change", null, MyAPIGateway.Utilities.SerializeToBinary(data));
                        }
                        else
                        {
                            ChangeTargetElevation(UP_1);
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Climb1Action);
                }

                #endregion

                #region Action Drop 1m

                if (!actions.Exists(x => x.Id == ID_ZepDrop1))
                {
                    Drop1Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_ZepDrop1);
                    Drop1Action.Name.Append(ID_ZepDrop1);
                    Drop1Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                    Drop1Action.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    Drop1Action.Writer = (b, str) =>
                    {
                        ZeppelinController zep = b.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            str.Append($"{zep.Data.TargetAltitude.ToString("n3")}km");
                        }
                    };

                    Drop1Action.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {

                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = DOWN_1 };
                            Network.SendCommand("change", null, MyAPIGateway.Utilities.SerializeToBinary(data));
                        }
                        else
                        {
                            ChangeTargetElevation(DOWN_1);
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Drop1Action);
                }

                #endregion

                #region Action Climb 10m

                if (!actions.Exists(x => x.Id == ID_ZepClimb10))
                {
                    Climb10Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_ZepClimb10);
                    Climb10Action.Name.Append(ID_ZepClimb10);
                    Climb10Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                    Climb10Action.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    Climb10Action.Writer = (b, str) =>
                    {
                        ZeppelinController zep = b.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            str.Append($"{zep.Data.TargetAltitude.ToString("n3")}km");
                        }
                    };

                    Climb10Action.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = UP_10 };
                            Network.SendCommand("change", null, MyAPIGateway.Utilities.SerializeToBinary(data));
                        }
                        else
                        {
                            ChangeTargetElevation(UP_10);
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Climb10Action);
                }

                #endregion

                #region Action Drop 10m

                if (!actions.Exists(x => x.Id == ID_ZepDrop10))
                {
                    Drop10Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_ZepDrop10);
                    Drop10Action.Name.Append(ID_ZepDrop10);
                    Drop10Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                    Drop10Action.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    Drop10Action.Writer = (b, str) =>
                    {
                        ZeppelinController zep = b.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            str.Append($"{zep.Data.TargetAltitude.ToString("n3")}km");
                        }
                    };

                    Drop10Action.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = DOWN_10 };
                            Network.SendCommand("change", null, MyAPIGateway.Utilities.SerializeToBinary(data));
                        }
                        else
                        {
                            ChangeTargetElevation(DOWN_10);
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Drop10Action);
                }

                #endregion

                #region Action Climb 100m

                if (!actions.Exists(x => x.Id == ID_ZepClimb100))
                {
                    Climb100Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_ZepClimb100);
                    Climb100Action.Name.Append(ID_ZepClimb100);
                    Climb100Action.Icon = "Textures\\GUI\\Icons\\Actions\\Increase.dds";
                    Climb100Action.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    Climb100Action.Writer = (b, str) =>
                    {
                        ZeppelinController zep = b.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            str.Append($"{zep.Data.TargetAltitude.ToString("n3")}km");
                        }
                    };

                    Climb100Action.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = UP_100 };
                            Network.SendCommand("change", null, MyAPIGateway.Utilities.SerializeToBinary(data));
                        }
                        else
                        {
                            ChangeTargetElevation(UP_100);
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Climb100Action);
                }

                #endregion

                #region Action Drop 100m

                if (!actions.Exists(x => x.Id == ID_ZepDrop100))
                {
                    Drop100Action = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_ZepDrop100);
                    Drop100Action.Name.Append(ID_ZepDrop100);
                    Drop100Action.Icon = "Textures\\GUI\\Icons\\Actions\\Decrease.dds";
                    Drop100Action.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    Drop100Action.Writer = (b, str) =>
                    {
                        ZeppelinController zep = b.GameLogic.GetAs<ZeppelinController>();

                        if (zep != null)
                        {
                            str.Append($"{zep.Data.TargetAltitude.ToString("n3")}km");
                        }
                    };

                    Drop100Action.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = DOWN_100 };
                            Network.SendCommand("change", null, MyAPIGateway.Utilities.SerializeToBinary(data));
                        }
                        else
                        {
                            ChangeTargetElevation(DOWN_100);
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(Drop100Action);
                }

                #endregion

                #region Action Set Current Altitude

                if (!actions.Exists(x => x.Id == ID_SetCurrentAltitude))
                {
                    SetCurrentAction = MyAPIGateway.TerminalControls.CreateAction<IMyCockpit>(ID_SetCurrentAltitude);
                    SetCurrentAction.Name.Append(ID_SetCurrentAltitude);
                    SetCurrentAction.Writer = (b, str) => str.Append(ID_SetCurrentAltitude);
                    SetCurrentAction.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    SetCurrentAction.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            AltitudeAdjust data = new AltitudeAdjust() { BlockId = b.EntityId, AdjustmentAmount = UP_1 };
                            Network.SendCommand("reset", null, MyAPIGateway.Utilities.SerializeToBinary(b.EntityId));
                        }
                        else
                        {
                            ResetTargetElevation();
                        }
                    };

                    MyAPIGateway.TerminalControls.AddAction<IMyCockpit>(SetCurrentAction);
                }

                #endregion

                #region Action OnOff Controller

                if (!actions.Exists(x => x.Id == ID_ZepOnOff))
                {
                    ZeppelinOnOffAction = MyAPIGateway.TerminalControls.CreateAction<Sandbox.ModAPI.Ingame.IMyCockpit>(ID_ZepOnOff);
                    ZeppelinOnOffAction.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    ZeppelinOnOffAction.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            Network.SendCommand("toggle_active", null, MyAPIGateway.Utilities.SerializeToBinary(b.EntityId));
                        }
                        else
                        {
                            ToggleActive();
                        }
                    };
                    ZeppelinOnOffAction.Name = new StringBuilder("Zeppelin Controller On/Off");
                    ZeppelinOnOffAction.Writer = ActiveHotbarText;
                    MyAPIGateway.TerminalControls.AddAction<Sandbox.ModAPI.Ingame.IMyCockpit>(ZeppelinOnOffAction);
                }

                #endregion

                #region Action Setup Zeppelin Controller

                if (!actions.Exists(x => x.Id == ID_ZepSetup))
                {
                    SetupAction = MyAPIGateway.TerminalControls.CreateAction<Sandbox.ModAPI.Ingame.IMyCockpit>(ID_ZepSetup);
                    SetupAction.Enabled = (b) => { return b.GameLogic.GetAs<ZeppelinController>() != null; };
                    SetupAction.Action = (b) =>
                    {
                        if (!MyAPIGateway.Multiplayer.IsServer)
                        {
                            Network.SendCommand("setup", null, MyAPIGateway.Utilities.SerializeToBinary(b.EntityId));
                        }
                        else
                        {
                            ZeppSetup();
                        }
                    };

                    SetupAction.Name = new StringBuilder("Run Setup");
                    SetupAction.Writer = (b, sb) => sb.Append("Run Setup");
                    MyAPIGateway.TerminalControls.AddAction<Sandbox.ModAPI.Ingame.IMyCockpit>(SetupAction);
                }

                #endregion
            }
        }
    }
}
