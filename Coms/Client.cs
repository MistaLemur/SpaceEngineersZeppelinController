using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace ZepController.Coms
{
    public class Client : ICommunicate
    {
        public event Action<Command> OnCommandRecived = delegate { };
        /// <summary>
        /// Terminal string, isServer
        /// </summary>
        public event Action<string> OnTerminalInput = delegate { };

        private ushort ModId;
        private string Keyword;

        public MultiplayerTypes MultiplayerType => Server.GetMultiplayerType();

        /// <summary>
        /// Handles communication with the server
        /// </summary>
        /// <param name="modId">Identifies what communications are picked up by this mod</param>
        /// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
        public Client(ushort modId, string keyword = null)
        {
            ModId = modId;

            if(keyword != null)
                Keyword = keyword.ToLower();

            MyAPIGateway.Multiplayer.RegisterMessageHandler(this.ModId, HandleMessage);
        }

        public void Close()
        {
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(this.ModId, HandleMessage);
        }

        private void HandleChatInput(string messageText, ref bool sendToOthers)
        {
            string[] args = messageText.Split(' ');
            if (args[0].ToLower() != Keyword) return;
            sendToOthers = false;

            OnTerminalInput.Invoke(messageText.Substring(Keyword.Length).Trim(' '));
        }

        private void HandleMessage(byte[] msg)
        {
            try
            {
                Command cmd = MyAPIGateway.Utilities.SerializeFromBinary<Command>(msg);

                if (cmd != null)
                {
                    OnCommandRecived.Invoke(cmd);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// One of two methods for sending server messages
        /// </summary>
        /// <param name="arguments">Command argument string</param>
        /// <param name="message">Text for display purposes</param>
        /// <param name="steamId">Player Identifier</param>
        public void SendCommand(string arguments, string message = null, ulong steamId = ulong.MinValue)
        {
            SendCommand(new Command {Arguments = arguments, Message = message }, steamId);
        }

        public void SendCommand(Command cmd, ulong steamId = ulong.MinValue)
        {
            cmd.SteamId = MyAPIGateway.Session.Player.SteamUserId;
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(cmd);

            MyAPIGateway.Multiplayer.SendMessageToServer(ModId, data, true);
        }
    }
}
