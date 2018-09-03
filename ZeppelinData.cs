using ProtoBuf;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game.Components;
using VRage.ModAPI;

namespace ZepController
{
    [ProtoContract]
    public class ZeppelinData
    {
        [ProtoMember]
        public long BlockId { get; set; }

        [ProtoMember]
        public float TargetAltitude { get; set; }

        [ProtoMember]
        public bool IsActive { get; set; }
    }
}
