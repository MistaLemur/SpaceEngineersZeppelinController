using ProtoBuf;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using System.Collections.Generic;
using VRage.Game.ModAPI;

namespace ZepController
{
    [ProtoContract]
    public class ZeppelinDefinition
    {
        [ProtoMember]
        public long GridId { get; set; }

        [ProtoMember]
        public float TargetAltitude { get; set; }

        [ProtoMember]
        public bool IsActive { get; set; }


        public bool IsSetup = false;
        public bool HasLCD = false;
        public bool HasExhaust = false;
        public bool DockFilled = false;
        public bool IsDocked = false;
        public string LoadedConfig = "";

        public IMyCubeGrid Grid;

        public List<IMyGasTank>        Balloons = new List<IMyGasTank>();
        public List<IMyGasTank>        Ballasts = new List<IMyGasTank>();
        public List<IMyOxygenFarm>     OxygenFarms = new List<IMyOxygenFarm>();
        public List<IMyThrust>         Exhaust = new List<IMyThrust>();
        public List<IMyLandingGear>    Gears = new List<IMyLandingGear>();
        public List<IMyShipConnector>  Connectors = new List<IMyShipConnector>();
        public List<IMyGyro>           Gyros = new List<IMyGyro>();
        //public List<IMyCockpit>        OtherCockpits = new List<IMyCockpit>();
        public List<IMyTextPanel>      LCDs = new List<IMyTextPanel>();
    }
}
