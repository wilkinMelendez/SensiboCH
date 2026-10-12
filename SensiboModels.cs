using System.Collections.Generic;
using Newtonsoft.Json;

namespace SensiboCrestronHome
{
    /// <summary>Respuesta genérica de la API v2 de Sensibo: { "status": "success", "result": ... }</summary>
    public class SensiboResponse<T>
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("result")] public T Result { get; set; }
    }

    public class SensiboPod
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("room")] public SensiboRoom Room { get; set; }
        [JsonProperty("acState")] public SensiboAcState AcState { get; set; }
        [JsonProperty("measurements")] public SensiboMeasurements Measurements { get; set; }
        [JsonProperty("connectionStatus")] public SensiboConnectionStatus ConnectionStatus { get; set; }
    }

    public class SensiboRoom
    {
        [JsonProperty("name")] public string Name { get; set; }
    }

    public class SensiboConnectionStatus
    {
        [JsonProperty("isAlive")] public bool IsAlive { get; set; }
    }

    public class SensiboMeasurements
    {
        [JsonProperty("temperature")] public double? Temperature { get; set; }   // Siempre en °C
        [JsonProperty("humidity")] public double? Humidity { get; set; }
    }

    public class SensiboAcState
    {
        [JsonProperty("on")] public bool On { get; set; }

        /// <summary>cool | heat | fan | dry | auto (depende de las capacidades del control remoto)</summary>
        [JsonProperty("mode")] public string Mode { get; set; }

        /// <summary>auto | quiet | low | medium_low | medium | medium_high | high | strong</summary>
        [JsonProperty("fanLevel", NullValueHandling = NullValueHandling.Ignore)]
        public string FanLevel { get; set; }

        [JsonProperty("targetTemperature", NullValueHandling = NullValueHandling.Ignore)]
        public int? TargetTemperature { get; set; }

        /// <summary>"C" o "F"</summary>
        [JsonProperty("temperatureUnit", NullValueHandling = NullValueHandling.Ignore)]
        public string TemperatureUnit { get; set; }

        [JsonProperty("swing", NullValueHandling = NullValueHandling.Ignore)]
        public string Swing { get; set; }
    }

    /// <summary>remoteCapabilities: modos que soporta el control remoto del AC y, por modo, sus niveles de ventilador.</summary>
    public class SensiboRemoteCapabilities
    {
        [JsonProperty("modes")] public Dictionary<string, SensiboModeCapabilities> Modes { get; set; }
    }

    public class SensiboModeCapabilities
    {
        [JsonProperty("fanLevels")] public List<string> FanLevels { get; set; }
    }

    public class SensiboPodCapabilities
    {
        [JsonProperty("remoteCapabilities")] public SensiboRemoteCapabilities RemoteCapabilities { get; set; }
    }

    public static class SensiboModes
    {
        public static readonly IList<string> All = new[] { "cool", "heat", "fan", "dry", "auto" };
    }

    /// <summary>Todos los niveles que usa la API de Sensibo; cada AC soporta solo algunos.</summary>
    public static class SensiboFanLevels
    {
        public static readonly IList<string> All = new[] { "auto", "quiet", "low", "medium_low", "medium", "medium_high", "high", "strong" };
    }
}
