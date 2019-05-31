using ProtoBuf;

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

    [ProtoContract]
    public class AltitudeAdjust
    {
        [ProtoMember]
        public long BlockId { get; set; }

        [ProtoMember]
        public float AdjustmentAmount { get; set; }
    }
}
