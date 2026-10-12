using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using Crestron.SimplSharp.CrestronIO.Compression;
using Crestron.SimplSharp.Net;
using Crestron.SimplSharp.Net.Https;
using Newtonsoft.Json;

namespace SensiboCrestronHome
{
    /// <summary>
    /// Cliente mínimo para la API cloud v2 de Sensibo (https://home.sensibo.com/api/v2).
    /// Pide respuestas gzip: Sensibo da un límite de peticiones más alto a los clientes que las aceptan.
    /// </summary>
    public class SensiboApiClient : IDisposable
    {
        private const string BaseUrl = "https://home.sensibo.com/api/v2";
        private readonly HttpsClient _client;
        private readonly CCriticalSection _lock = new CCriticalSection();
        private readonly string _apiKey;

        public SensiboApiClient(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new ArgumentException("API key required");
            _apiKey = apiKey;
            _client = new HttpsClient();
            _client.KeepAlive = true;
            _client.TimeoutEnabled = true;
            _client.Timeout = 10;
            _client.PeerVerification = false;
            _client.HostVerification = false;
        }

        public List<SensiboPod> GetPods()
        {
            var json = Send(RequestType.Get, "/users/me/pods?fields=id,room", null);
            return JsonConvert.DeserializeObject<SensiboResponse<List<SensiboPod>>>(json).Result;
        }

        public SensiboPod GetPod(string podId)
        {
            var json = Send(RequestType.Get,
                "/pods/" + podId + "?fields=id,room,acState,measurements,connectionStatus", null);
            return JsonConvert.DeserializeObject<SensiboResponse<SensiboPod>>(json).Result;
        }

        public SensiboRemoteCapabilities GetCapabilities(string podId)
        {
            var json = Send(RequestType.Get, "/pods/" + podId + "?fields=remoteCapabilities", null);
            var pod = JsonConvert.DeserializeObject<SensiboResponse<SensiboPodCapabilities>>(json).Result;
            return pod == null ? null : pod.RemoteCapabilities;
        }

        /// <summary>Cambia una sola propiedad del acState (on, mode, fanLevel, targetTemperature...).</summary>
        public void SetAcProperty(string podId, string property, object newValue)
        {
            var body = JsonConvert.SerializeObject(new { newValue = newValue });
            Send(RequestType.Patch, "/pods/" + podId + "/acStates/" + property, body);
        }

        private string Send(RequestType type, string path, string body)
        {
            var separator = path.Contains("?") ? "&" : "?";
            var request = new HttpsClientRequest();
            request.Url.Parse(BaseUrl + path + separator + "apiKey=" + _apiKey);
            request.RequestType = type;
            request.Header.ContentType = "application/json";
            request.Header.SetHeaderValue("Accept-Encoding", "gzip");
            if (body != null) request.ContentString = body;

            _lock.Enter();
            try
            {
                var response = _client.Dispatch(request);
                if (response == null)
                    throw new SensiboApiException("No response from Sensibo", 0);

                var content = ReadContent(response);
                if (response.Code < 200 || response.Code >= 300)
                    throw new SensiboApiException("HTTP " + response.Code + ": " + content, response.Code);
                return content;
            }
            finally
            {
                _lock.Leave();
            }
        }

        /// <summary>Devuelve el cuerpo como texto, descomprimiendo gzip si hace falta (detecta la firma 1F 8B).</summary>
        private static string ReadContent(HttpsClientResponse response)
        {
            var bytes = response.ContentBytes;
            if (bytes == null || bytes.Length == 0)
                return response.ContentString ?? string.Empty;

            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using (var input = new MemoryStream(bytes))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                        output.Write(buffer, 0, read);
                    var data = output.ToArray();
                    return Encoding.UTF8.GetString(data, 0, data.Length);
                }
            }

            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    public class SensiboApiException : Exception
    {
        public int StatusCode { get; private set; }

        public SensiboApiException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
