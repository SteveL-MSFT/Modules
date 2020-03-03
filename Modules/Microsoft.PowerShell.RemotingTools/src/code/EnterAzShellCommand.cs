// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.RemotingTools
{
    #region JsonTypes

    public class IntToStringConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                ReadOnlySpan<byte> span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
                if (Utf8Parser.TryParse(span, out int number, out int bytesConsumed) && span.Length == bytesConsumed)
                {
                    return number;
                }

                if (int.TryParse(reader.GetString(), out number))
                {
                    return number;
                }
            }

            return reader.GetInt32();
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    public class CloudShellTerminal
    {
        public string id { get; set; }
        public string socketUri { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int idleTimeout { get; set; }
        public bool tokenUpdated { get; set; }
        public string rootDirectory { get; set; }
    }

    public class AuthResponse
    {
        public string token_type { get; set; }
        public string scope { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int expires_in { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int ext_expires_in { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int not_before { get; set; }
        public string resource { get; set; }
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public string id_token { get; set; }
    }

    public class AuthResponsePending
    {
        public string error { get; set; }
        public string error_description { get; set; }
        public int[] error_codes { get; set; }
        public string timestamp { get; set; }
        public string trace_id { get; set; }
        public string correlation_id { get; set; }
        public string error_uri { get; set; }
    }

    public class CloudShellResponse
    {
        public Dictionary<string,string> properties { get; set; }
    }

    public class DeviceCodeResponse
    {
        public string user_code { get; set; }
        public string device_code { get; set; }
        public string verification_url { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int expires_in { get; set; }
        [JsonConverter(typeof(IntToStringConverter))]
        public int interval { get; set; }
        public string message { get; set; }
    }

    public class AzureTenant
    {
        public string id { get; set; }
        public string tenantId { get; set; }
        public string countryCode { get; set; }
        public string displayName { get; set; }
        public string[] domains { get; set; }
    }

    public class AzureTenantResponse
    {
        public AzureTenant[] value { get; set; }
    }

    #endregion

    [Cmdlet(VerbsCommon.Enter, "AzShell")]
    public sealed class EnterAzShellCommand : PSCmdlet
    {
        public enum ShellType
        {
            PowerShell,
            Bash
        }

        [Parameter()]
        public Guid TenantId;

        [Parameter()]
        public SwitchParameter Reset;

        [Parameter()]
        public ShellType Shell {
            get
            {
                return _shellType;
            }

            set
            {
                _shellType = value;
            }
        }

        private static HttpClient _httpClient;
        private static string _accessToken;
        private static string _refreshToken;
        private static ShellType _shellType = ShellType.PowerShell;
        private static ConcurrentQueue<byte[]> _inputQueue;
        private static CancellationTokenSource _cancelTokenSource;
        private static ClientWebSocket _socket;
        private const int BufferSize = 4096;
        private static bool _stopProcessing = false;
        private const string ClientId = "245e1dee-74ef-4257-a8c8-8208296e1dfd"; //"e802b08b-2e1d-4cda-94b6-559b8b3f8dd6";
        private const string CommonTenant = "common";
        private const string userAgent = "PowerShell.Enter-AzShell";
        private static readonly string[] Scopes = new string[]{"https://management.azure.com/user_impersonation"};

        protected override void BeginProcessing()
        {
            _httpClient = new HttpClient();
            _inputQueue = new ConcurrentQueue<byte[]>();
            _socket = new ClientWebSocket();
            _socket.Options.SetBuffer(receiveBufferSize: BufferSize, sendBufferSize: BufferSize);
            _cancelTokenSource = new CancellationTokenSource();
        }

        protected override void StopProcessing()
        {
            _stopProcessing = true;
            _cancelTokenSource.Cancel();
        }

        protected override void ProcessRecord()
        {
            WriteVerbose("Authenticating with Azure...");
            GetDeviceCode();

            string tenantId;
            if (TenantId != Guid.Empty)
            {
                tenantId = TenantId.ToString();
            }
            else
            {
                tenantId = GetTenantId();
            }

            RefreshToken(tenantId);

            WriteVerbose("Requesting Cloud Shell...");
            string cloudShellUri = RequestCloudShell();

            WriteVerbose("Connecting terminal...");
            string socketUri = RequestTerminal(cloudShellUri);

            ConnectWebSocket(socketUri);
            Task.Run(() => ReceiveWebSocket());

            var inputThread = new Thread(() => InputThread(_cancelTokenSource.Token));
            inputThread.Name = "Enter-AzShell Input Thread";
            inputThread.Start();

            while(_socket.State == WebSocketState.Open || _socket.State == WebSocketState.Connecting)
            {
                var buffer = new List<byte>();

                while(_inputQueue.Count > 0)
                {
                    if (_inputQueue.TryDequeue(out var input))
                    {
                        buffer.AddRange(input);
                    }
                }

                if (buffer.Count > 0)
                {
                    Task.Run(() => _socket.SendAsync(
                        new ArraySegment<byte>(buffer.ToArray()),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        _cancelTokenSource.Token
                    ));
                }

                Thread.Sleep(5);
            }

            _cancelTokenSource.Cancel();
            Task.Run(() => _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None));
            WriteVerbose($"Session closed: {_socket.State} => {_socket.CloseStatus} : {_socket.CloseStatusDescription}");
        }

        private static void InputThread(CancellationToken ct)
        {
            var stdin = Console.OpenStandardInput(BufferSize);
            var buffer = new byte[BufferSize];

            while (!ct.IsCancellationRequested)
            {
                // check if a key is available before trying to read stdin which blocks
                if (Console.KeyAvailable)
                {
                    var bytesRead = stdin.Read(buffer, 0, BufferSize);
                    var bytes = new byte[bytesRead];
                    Array.Copy(buffer, bytes, bytesRead);
                    _inputQueue.Enqueue(bytes);
                }
                Thread.Sleep(5);
            }
        }

        private static void ConnectWebSocket(string socketUri)
        {
            Task.Run(() => _socket.ConnectAsync(new Uri(socketUri), _cancelTokenSource.Token)).Wait();

            while (_socket.State == WebSocketState.Connecting)
            {
                Thread.Sleep(50);
            }
        }

        private static async void ReceiveWebSocket()
        {
            var incomingBuffer = new Byte[4096];
            while (!_stopProcessing && !_cancelTokenSource.IsCancellationRequested)
            {
                try
                {
                    WebSocketReceiveResult result = null;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(incomingBuffer), _cancelTokenSource.Token);
                        if (result.Count > 0)
                        {
                            byte[] bytes = new byte[result.Count];
                            Array.Copy(incomingBuffer.ToArray(), bytes, result.Count);
                            Console.Write(Encoding.UTF8.GetString(incomingBuffer.ToArray(), 0, result.Count));
                            Array.Clear(incomingBuffer, 0, result.Count);
                        }
                    }
                    while (!result.EndOfMessage);

                    Thread.Sleep(5);
                }
                catch
                {
                    _cancelTokenSource.Cancel();
                    return;
                }
            }
        }

        private static string SendWebRequest(string resourceUri, string body, string contentType, HttpMethod method, string token = null, bool ignoreError = false)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            if (token != null)
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            }

            var request = new HttpRequestMessage()
            {
                RequestUri = new Uri(resourceUri),
                Method = method
            };
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            string response = string.Empty;

            _httpClient.SendAsync(request)
                .ContinueWith(responseTask =>
                {
                    //Console.WriteLine("Result:" + responseTask.Result.ToString());
                    Task<string> task = Task.Run<string>(async () => await responseTask.Result.Content.ReadAsStringAsync());
                    response = task.Result;
                    //Console.WriteLine($"Response: {response}");

                    if (!ignoreError && !responseTask.Result.IsSuccessStatusCode)
                    {
                        throw new Exception(responseTask.Result.ToString());
                    }
                }).Wait();

            return response;
        }

        private static void RefreshToken(string tenantId)
        {
            string resourceUri = $"https://login.microsoftonline.com/{tenantId}/oauth2/token";
            const string resource = "https://management.core.windows.net/";
            string encodedResource = Uri.EscapeDataString(resource);
            string body = $"client_id={ClientId}&resource={encodedResource}&grant_type=refresh_token&refresh_token={_refreshToken}";

            string response = SendWebRequest(
                resourceUri: resourceUri,
                body: body,
                contentType: "application/x-www-form-urlencoded",
                method: HttpMethod.Post,
                ignoreError: true
            );

            var authResponse = ConvertFromJson<AuthResponse>(response);
            _accessToken = authResponse.access_token;
            _refreshToken = authResponse.refresh_token;
        }

        private static T ConvertFromJson<T>(string json)
        {
            var readOnlySpan = new ReadOnlySpan<byte>(Encoding.Default.GetBytes(json));
            return JsonSerializer.Deserialize<T>(readOnlySpan);
        }

        private static string RequestTerminal(string uri)
        {
            string shell = "pwsh";
            if (_shellType == ShellType.Bash)
            {
                shell = "bash";
            }

            string resourceUri = $"{uri}/terminals?cols={Console.WindowWidth}&rows={Console.WindowHeight}&version=2019-01-01&shell={shell}";
            string response = SendWebRequest(
                resourceUri: resourceUri,
                body: string.Empty,
                contentType: "application/json",
                token: _accessToken,
                method: HttpMethod.Post
            );

            var terminal = ConvertFromJson<CloudShellTerminal>(response);

            return terminal.socketUri;
        }

        private static string GetTenantId()
        {
            const string resourceUri = "https://management.azure.com/tenants?api-version=2018-01-01";
            string response = SendWebRequest(
                resourceUri: resourceUri,
                body: null,
                contentType: null,
                token: _accessToken,
                method: HttpMethod.Get
            );

            var readOnlySpan = new ReadOnlySpan<byte>(Encoding.Default.GetBytes(response));
            var tenant = JsonSerializer.Deserialize<AzureTenantResponse>(readOnlySpan);

            if (tenant.value.Length == 0)
            {
                throw new Exception("No tenants found!");
            }

            return tenant.value[0].tenantId;
        }

        private static string RequestCloudShell()
        {
            const string resourceUri = "https://management.azure.com/providers/Microsoft.Portal/consoles/default?api-version=2018-10-01";
            const string body = @"
                {
                    ""Properties"": {
                        ""consoleRequestProperties"": {
                        ""osType"": ""linux""
                        }
                    }
                }
                ";

            string response = SendWebRequest(
                resourceUri: resourceUri,
                body: body,
                contentType: "application/json",
                token: _accessToken,
                method: HttpMethod.Put
            );

            var readOnlySpan = new ReadOnlySpan<byte>(Encoding.Default.GetBytes(response));
            var cloudShellResponse = JsonSerializer.Deserialize<CloudShellResponse>(readOnlySpan);

            return cloudShellResponse.properties["uri"];
        }


        private static void GetDeviceCode()
        {
            string resourceUri = "https://login.microsoftonline.com/common/oauth2/devicecode";
            const string resource = "https://management.core.windows.net/";
            string encodedResource = Uri.EscapeDataString(resource);
            string body = $"client_id={ClientId}&resource={encodedResource}";
            string response = SendWebRequest(
                resourceUri: resourceUri,
                body: body,
                contentType: "application/x-www-form-urlencoded",
                method: HttpMethod.Post
            );

            var deviceCode = ConvertFromJson<DeviceCodeResponse>(response);
            Console.WriteLine(deviceCode.message);

            resourceUri = "https://login.microsoftonline.com/common/oauth2/token";
            body = $"grant_type=device_code&resource={encodedResource}&client_id={ClientId}&code={deviceCode.device_code}";
            // poll until user authenticates
            for (int count = 0; count < deviceCode.expires_in / deviceCode.interval; count++)
            {
                if (_stopProcessing)
                {
                    throw new Exception("Cmdlet stopped.");
                }

                response = SendWebRequest(
                    resourceUri: resourceUri,
                    body: body,
                    contentType: "application/x-www-form-urlencoded",
                    method: HttpMethod.Post,
                    ignoreError: true
                );

                var authResponsePending = ConvertFromJson<AuthResponsePending>(response);
                if (authResponsePending.error == null)
                {
                    var authResponse = ConvertFromJson<AuthResponse>(response);
                    _accessToken = authResponse.access_token;
                    _refreshToken = authResponse.refresh_token;
                    return;
                }

                if (!authResponsePending.error.Equals("authorization_pending"))
                {
                    throw new Exception($"Authentication failed: {authResponsePending.error_description}");
                }

                Thread.Sleep(deviceCode.interval * 1000);
            }
        }
    }
}