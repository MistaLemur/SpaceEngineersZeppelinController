﻿using ProtoBuf;

namespace ZepController.Coms
{
    [ProtoContract]
    public class Command
    {
        [ProtoMember]
        public ulong SteamId { get; set; }

        [ProtoMember]
        public string Message { get; set; }

        [ProtoMember]
        public string Arguments { get; set; }

        [ProtoMember]
        public string DataType { get; set; }

        [ProtoMember]
        public string XMLData { get; set; }
    }
}
