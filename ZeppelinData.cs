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
}
