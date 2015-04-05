using Lib_K_Relay.Crypto;
using Lib_K_Relay.Networking.Packets;
using Lib_K_Relay.Networking.Packets.Client;
using Lib_K_Relay.Networking.Packets.DataObjects;
using Lib_K_Relay.Networking.Packets.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Lib_K_Relay.Networking
{
    public class Client
    {
        public int Time = 0;
        public int ObjectId;
        public PlayerData PlayerData;

        public RC4 ClientReceiveKey;
        public RC4 ServerReceiveKey;
        public RC4 ClientSendKey;
        public RC4 ServerSendKey;

        private PacketBuffer _localBuffer = new PacketBuffer();
        private PacketBuffer _remoteBuffer = new PacketBuffer();

        private TcpClient _localConnection;
        private TcpClient _remoteConnection;
        private Proxy _proxy;

        public Client(Proxy proxy, TcpClient client)
        {
            _proxy = proxy;
            _localConnection = client;
            _remoteConnection = new TcpClient();

            ClientReceiveKey = new RC4(_proxy.Key0);
            ServerReceiveKey = new RC4(_proxy.Key1);
            ClientSendKey = new RC4(_proxy.Key1);
            ServerSendKey = new RC4(_proxy.Key0);

            _remoteConnection.BeginConnect(
                IPAddress.Parse(_proxy.RemoteAddress),
                _proxy.Port, RemoteConnected, null);
        }

        private void RemoteConnected(IAsyncResult ar)
        {
            _remoteConnection.EndConnect(ar);

            Console.WriteLine("[Client Listener] Client connected.");
            _proxy.FireClientConnected(this);

            BeginRemoteRead(0, 4); // Read 4 bytes (packet size)
            BeginLocalRead(0, 4); // Read 4 bytes (packet size)
        }

        private void RemoteReceive(IAsyncResult ar)
        {
            try
            {
                NetworkStream stream = _remoteConnection.GetStream();
                _remoteBuffer.Advance(stream.EndRead(ar));

                if (_remoteBuffer.Index() == 4)
                { // We have the first four bytes
                    // Resize the receive buffer.
                    _remoteBuffer.Resize(IPAddress.NetworkToHostOrder(
                        BitConverter.ToInt32(_remoteBuffer.Buffer(), 0)));

                    BeginRemoteRead(_remoteBuffer.Index(), _remoteBuffer.BytesRemaining());
                }
                else if (_remoteBuffer.BytesRemaining() > 0)
                { // Awaiting the rest of the packet
                    BeginRemoteRead(_remoteBuffer.Index(), _remoteBuffer.BytesRemaining());
                }
                else
                { // We have the full packet
                    ServerReceiveKey.Cipher(_remoteBuffer.Buffer());
                    Packet packet = Packet.CreateInstance(_remoteBuffer.Buffer());

                    if (packet.Type != PacketType.UNKNOWN)
                    {
                        HandlePacketInternal(packet);
                        _proxy.FireServerPacket(this, packet);
                    }

                    if (packet.Send)
                        SendToClient(packet);

                    // Reset our counters and recieve a new one.
                    _remoteBuffer.Flush();
                    BeginRemoteRead(0, 4);
                }
            } catch (Exception e) { Close(e.Message); }
        }

        private void LocalReceive(IAsyncResult ar)
        {
            try
            {
                NetworkStream stream = _localConnection.GetStream();
                _localBuffer.Advance(stream.EndRead(ar));

                if (_localBuffer.Length() == 4)
                { // We have the first four bytes
                    // Resize the receive buffer
                    _localBuffer.Resize(IPAddress.NetworkToHostOrder(
                        BitConverter.ToInt32(_localBuffer.Buffer(), 0)));

                    BeginLocalRead(_localBuffer.Index(), _localBuffer.BytesRemaining());
                }
                else if (_localBuffer.BytesRemaining() > 0)
                { // Awaiting the rest of the packet
                    BeginLocalRead(_localBuffer.Index(), _localBuffer.BytesRemaining());
                }
                else
                { // We have the full packet
                    ClientReceiveKey.Cipher(_localBuffer.Buffer());
                    Packet packet = Packet.CreateInstance(_localBuffer.Buffer());

                    if (packet.Type != PacketType.UNKNOWN)
                    {
                        HandlePacketInternal(packet);
                        _proxy.FireClientPacket(this, packet);
                    }

                    if (packet.Send)
                        SendToServer(packet);

                    // Reset our counters and recieve a new one
                    _localBuffer.Flush();
                    BeginLocalRead(0, 4);
                }
            } catch (Exception e) { Close(e.Message); }
        }

        private void BeginLocalRead(int offset, int amount)
        {
            _localConnection.GetStream().BeginRead(
                _localBuffer.Buffer(), offset, amount, LocalReceive, null);
        }

        private void BeginRemoteRead(int offset, int amount)
        {
            _remoteConnection.GetStream().BeginRead(
                _remoteBuffer.Buffer(), offset, amount, RemoteReceive, null);
        }

        public void SendToServer(Packet packet)
        {
            MemoryStream ms = new MemoryStream();
            using (PacketWriter w = new PacketWriter(ms))
            {
                w.Write((int)0);
                w.Write(packet.Id);
                packet.Write(w);
            }

            byte[] data = ms.ToArray();
            PacketWriter.BlockCopyInt32(data, data.Length);
            ServerSendKey.Cipher(data);

            NetworkStream remote = _remoteConnection.GetStream();
            remote.BeginWrite(data, 0, data.Length, (ar) => remote.EndWrite(ar), null);
        }

        public void SendToClient(Packet packet)
        {
            if (!_localConnection.Connected || !_remoteConnection.Connected) return;

            MemoryStream ms = new MemoryStream();
            using (PacketWriter w = new PacketWriter(ms))
            {
                w.Write((int)0);
                w.Write(packet.Id);
                packet.Write(w);
            }

            byte[] data = ms.ToArray();
            PacketWriter.BlockCopyInt32(data, data.Length);
            ClientSendKey.Cipher(data);

            NetworkStream local = _localConnection.GetStream();
            local.BeginWrite(data, 0, data.Length, (ar) => local.EndWrite(ar), null);
        }

        public void Close(string reason)
        {
            if (_remoteConnection.Connected || _localConnection.Connected)
            {
                _proxy.FireClientDisconnected(this);
                Console.WriteLine("[Client Handler] Client disconnected. {0}", reason);
            }

            if (_remoteConnection.Connected) _remoteConnection.Close();
            if (_localConnection.Connected) _localConnection.Close();
        }

        private void HandlePacketInternal(Packet packet) // TODO: Add more hooks
        {
            if (packet.Type == PacketType.CREATE_SUCCESS)
            {
                ObjectId = (packet as CreateSuccessPacket).ObjectId;
                PlayerData = new PlayerData(ObjectId);
            }
            else if (packet.Type == PacketType.UPDATE)
            {
                PlayerData.Parse(packet as UpdatePacket);
            }
            else if (packet.Type == PacketType.NEW_TICK)
            {
                PlayerData.Parse(packet as NewTickPacket);
            }
            else if (packet.Type == PacketType.PONG)
            {
                Time = (packet as PongPacket).Time;
            }
            else if (packet.Type == PacketType.MOVE)
            {
                Time = (packet as MovePacket).Time;
                PlayerData.Pos = (packet as MovePacket).NewPosition;
            }
            //else if (packet.Type == PacketType.MAPINFO)
            //{
            //    PlayerData.MapName = (packet as MapInfoPacket).Name;
            //}
        }
    }
}