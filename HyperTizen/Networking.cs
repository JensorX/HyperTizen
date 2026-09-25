using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.FlatBuffers;
using hyperhdrnet;

namespace HyperTizen
{
    public static class Networking
    {
        private const int MaxReplySize = 1024 * 1024;
        private static readonly object _lock = new object();
        private static readonly SemaphoreSlim _imageSendLock = new SemaphoreSlim(1, 1);
        private static TcpClient _client;
        private static NetworkStream _stream;

        public static TcpClient client
        {
            get { lock (_lock) { return _client; } }
            set { lock (_lock) { _client = value; } }
        }

        public static NetworkStream stream
        {
            get { lock (_lock) { return _stream; } }
            set { lock (_lock) { _stream = value; } }
        }

        public static void DisconnectClient()
        {
            lock (_lock)
            {
                try
                {
                    _stream?.Close();
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"DisconnectClient: Stream close error: {ex.Message}");
                }

                try
                {
                    _client?.Close();
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"DisconnectClient: Client close error: {ex.Message}");
                }

                _stream = null;
                _client = null;
                Helper.Log.Write(Helper.eLogType.Info, "DisconnectClient: Client and stream nulled");
            }
        }

        public static void SendRegister()
        {
            try
            {
                if (string.IsNullOrEmpty(Globals.Instance.ServerIp) || Globals.Instance.ServerPort <= 0)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"TCP FAILED: Bad config {Globals.Instance.ServerIp ?? "null"}:{Globals.Instance.ServerPort}");
                    return;
                }

                Helper.Log.Write(Helper.eLogType.Info,
                    $"TCP: Connecting to {Globals.Instance.ServerIp}:{Globals.Instance.ServerPort}");

                lock (_lock)
                {
                    _client = new TcpClient(Globals.Instance.ServerIp, Globals.Instance.ServerPort)
                    {
                        NoDelay = true
                    };
                    _stream = _client.GetStream();

                    byte[] registrationMessage = CreateRegistrationMessage();
                    if (registrationMessage == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Error,
                            "TCP FAILED: No FlatBuffer registration message");
                        return;
                    }

                    WriteLengthPrefix(_stream, registrationMessage.Length);
                    _stream.Write(registrationMessage, 0, registrationMessage.Length);
                    _stream.Flush();
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"TCP: Sent registration ({registrationMessage.Length} bytes), waiting for reply...");
                }

                ReadRegisterReply();
            }
            catch (SocketException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"SOCKET ERROR: {ex.Message} (Code:{ex.ErrorCode})");
                DisconnectClient();
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"TCP registration error: {ex.GetType().Name}: {ex.Message}");
                DisconnectClient();
            }
        }

        public static async Task<bool> SendImageAsync(byte[] yData, byte[] uvData, int width, int height)
        {
            await _imageSendLock.WaitAsync();
            try
            {
                lock (_lock)
                {
                    if (_client == null || _client.Client == null || !_client.Connected || _stream == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            "SendImageAsync: Connection is not ready");
                        return false;
                    }
                }

                if (yData == null || uvData == null || width <= 0 || height <= 0)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"SendImageAsync: Invalid frame data (dimensions={width}x{height}, " +
                        $"Y null={yData == null}, UV null={uvData == null})");
                    return false;
                }

                byte[] message = CreateFlatBufferMessage(yData, uvData, width, height);
                if (message == null)
                {
                    return false;
                }

                // One sender owns the whole request/reply exchange, preserving TCP frame boundaries.
                await SendMessageAndReceiveReplyAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"SendImageAsync: Frame transport failed: {ex.GetType().Name}: {ex.Message}");
                DisconnectClient();
                return false;
            }
            finally
            {
                _imageSendLock.Release();
            }
        }

        private static byte[] CreateFlatBufferMessage(byte[] yData, byte[] uvData, int width, int height)
        {
            lock (_lock)
            {
                if (_client == null || !_client.Connected || _stream == null)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "CreateFlatBufferMessage: Connection is not ready");
                    return null;
                }
            }

            long expectedYSize = (long)width * height;
            long expectedUVSize = expectedYSize / 2;
            if (expectedYSize > int.MaxValue || yData.Length != expectedYSize || uvData.Length != expectedUVSize)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"CreateFlatBufferMessage: Invalid NV12 buffers for {width}x{height} " +
                    $"(Y={yData.Length}/{expectedYSize}, UV={uvData.Length}/{expectedUVSize})");
                return null;
            }

            var builder = new FlatBufferBuilder(yData.Length + uvData.Length + 100);
            var yVector = NV12Image.CreateDataYVector(builder, yData);
            var uvVector = NV12Image.CreateDataUvVector(builder, uvData);

            NV12Image.StartNV12Image(builder);
            NV12Image.AddDataY(builder, yVector);
            NV12Image.AddDataUv(builder, uvVector);
            NV12Image.AddWidth(builder, width);
            NV12Image.AddHeight(builder, height);
            NV12Image.AddStrideY(builder, width);
            NV12Image.AddStrideUv(builder, width);
            var nv12Image = NV12Image.EndNV12Image(builder);

            Image.StartImage(builder);
            Image.AddDataType(builder, ImageType.NV12Image);
            Image.AddData(builder, nv12Image.Value);
            Image.AddDuration(builder, -1);
            var imageOffset = Image.EndImage(builder);

            Request.StartRequest(builder);
            Request.AddCommandType(builder, Command.Image);
            Request.AddCommand(builder, imageOffset.Value);
            var requestOffset = Request.EndRequest(builder);

            builder.Finish(requestOffset.Value);
            return builder.SizedByteArray();
        }

        public static byte[] CreateRegistrationMessage()
        {
            lock (_lock)
            {
                if (_client == null || !_client.Connected || _stream == null)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "CreateRegistrationMessage: Connection is not ready");
                    return null;
                }
            }

            var builder = new FlatBufferBuilder(256);
            var originOffset = builder.CreateString("HyperTizen");

            Register.StartRegister(builder);
            Register.AddPriority(builder, 123);
            Register.AddOrigin(builder, originOffset);
            var registerOffset = Register.EndRegister(builder);

            Request.StartRequest(builder);
            Request.AddCommandType(builder, Command.Register);
            Request.AddCommand(builder, registerOffset.Value);
            var requestOffset = Request.EndRequest(builder);

            builder.Finish(requestOffset.Value);
            return builder.SizedByteArray();
        }

        public static void ReadRegisterReply()
        {
            try
            {
                NetworkStream localStream;
                lock (_lock)
                {
                    if (_client == null || !_client.Connected || _stream == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Error, "ReadRegisterReply: No client/stream");
                        return;
                    }

                    localStream = _stream;
                    localStream.ReadTimeout = 5000;
                }

                byte[] payload = ReadReplyPayload(localStream);
                Reply reply = Reply.GetRootAsReply(new ByteBuffer(payload));
                if (reply.Registered > 0)
                {
                    Helper.Log.Write(Helper.eLogType.Info, "ReadRegisterReply: REGISTERED OK!");
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"ReadRegisterReply: NOT registered (code: {reply.Registered})");
                    DisconnectClient();
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"ReadRegisterReply failed: {ex.GetType().Name}: {ex.Message}");
                DisconnectClient();
            }
        }

        private static byte[] ReadReplyPayload(NetworkStream localStream)
        {
            byte[] header = new byte[4];
            if (!ReadExactly(localStream, header, 0, header.Length))
            {
                throw new EndOfStreamException("Connection closed before reply header");
            }

            int payloadLength = GetPayloadLength(header);
            byte[] payload = new byte[payloadLength];
            if (!ReadExactly(localStream, payload, 0, payload.Length))
            {
                throw new EndOfStreamException("Connection closed before complete reply");
            }

            return payload;
        }

        private static bool ReadExactly(NetworkStream localStream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = localStream.Read(buffer, offset + totalRead, count - totalRead);
                if (bytesRead == 0)
                {
                    return false;
                }

                totalRead += bytesRead;
            }

            return true;
        }

        private static async Task<bool> ReadExactlyAsync(
            NetworkStream localStream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = await localStream.ReadAsync(
                    buffer,
                    offset + totalRead,
                    count - totalRead,
                    cancellationToken);
                if (bytesRead == 0)
                {
                    return false;
                }

                totalRead += bytesRead;
            }

            return true;
        }

        private static int GetPayloadLength(byte[] header)
        {
            long payloadLength = ((long)header[0] << 24) |
                                 ((long)header[1] << 16) |
                                 ((long)header[2] << 8) |
                                 header[3];
            if (payloadLength <= 0 || payloadLength > MaxReplySize)
            {
                throw new InvalidDataException($"Invalid reply length: {payloadLength}");
            }

            return (int)payloadLength;
        }

        private static void WriteLengthPrefix(NetworkStream targetStream, int payloadLength)
        {
            byte[] header = new byte[4];
            header[0] = (byte)((payloadLength >> 24) & 0xFF);
            header[1] = (byte)((payloadLength >> 16) & 0xFF);
            header[2] = (byte)((payloadLength >> 8) & 0xFF);
            header[3] = (byte)(payloadLength & 0xFF);
            targetStream.Write(header, 0, header.Length);
        }

        private static async Task SendMessageAndReceiveReplyAsync(byte[] message)
        {
            NetworkStream localStream;
            lock (_lock)
            {
                if (_client == null || !_client.Connected || _stream == null)
                {
                    throw new IOException("Connection not ready for image frame");
                }

                localStream = _stream;
            }

            using (CancellationTokenSource timeout = new CancellationTokenSource(5000))
            {
                byte[] header = new byte[4];
                header[0] = (byte)((message.Length >> 24) & 0xFF);
                header[1] = (byte)((message.Length >> 16) & 0xFF);
                header[2] = (byte)((message.Length >> 8) & 0xFF);
                header[3] = (byte)(message.Length & 0xFF);

                await localStream.WriteAsync(header, 0, header.Length, timeout.Token);
                await localStream.WriteAsync(message, 0, message.Length, timeout.Token);
                await localStream.FlushAsync();

                byte[] replyHeader = new byte[4];
                if (!await ReadExactlyAsync(localStream, replyHeader, 0, replyHeader.Length, timeout.Token))
                {
                    throw new EndOfStreamException("Connection closed before image reply header");
                }

                int replyLength = GetPayloadLength(replyHeader);
                byte[] replyPayload = new byte[replyLength];
                if (!await ReadExactlyAsync(localStream, replyPayload, 0, replyPayload.Length, timeout.Token))
                {
                    throw new EndOfStreamException("Connection closed before complete image reply");
                }

                Reply reply = Reply.GetRootAsReply(new ByteBuffer(replyPayload));
                if (!string.IsNullOrEmpty(reply.Error))
                {
                    throw new IOException("Server rejected image: " + reply.Error);
                }
            }
        }
    }
}
