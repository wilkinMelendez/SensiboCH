using System;
using System.Collections.Generic;
using Crestron.RAD.Common.Attributes.Programming;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.Common.Interfaces.ExtensionDevice;
using Crestron.RAD.DeviceTypes.ExtensionDevice;
using Crestron.SimplSharp;

namespace SensiboCrestronHome
{
    /// <summary>
    /// Extension driver de Crestron Home para Sensibo (Sky / Air / Air Pro) vía la API cloud v2.
    /// Escrito con sintaxis C# 3 para mantener compatibilidad con la cadena de compilación de Crestron.
    /// </summary>
    public class SensiboThermostat : AExtensionDevice, ICloudConnected
    {
        #region Constantes

        // Atributos de usuario (configuración en Crestron Home Setup)
        private const string AttrApiKey = "sensiboApiKey";
        private const string AttrPodId = "sensiboPodId";
        private const string AttrPollSeconds = "sensiboPollSeconds";

        // Comandos de la UI
        private const string CmdTogglePower = "togglePower";
        private const string CmdRefresh = "refresh";

        // Propiedades enlazadas a la UI
        private const string PTileStatus = "tileStatus";
        private const string PTileIcon = "tileIcon";
        private const string PTileSecondaryIcon = "tileSecondaryIcon";
        private const string PStatusText = "statusText";
        private const string PPower = "powerOn";
        private const string PPowerLabel = "powerLabel";
        private const string PMode = "acMode";
        private const string PFan = "fanLevel";
        private const string PHumidity = "humidityLabel";
        private const string PHeatEnabled = "heatModeEnabled";
        private const string PCoolEnabled = "coolModeEnabled";
        private const string PAutoEnabled = "autoModeEnabled";
        private const string PUnits = "temperatureUnits";
        private const string PSetpointAuto = "setpointAuto";
        private const string PSetpointCool = "setpointCool";
        private const string PSetpointHeat = "setpointHeat";
        private const string PModeIcon = "modeIcon";
        private const string PCurrentTemp = "currentTemperature";
        private const string PCurrentTempLabel = "currentTemperatureLabel";

        private const string IconClimate = "icClimateRegular";
        private const string IconCooling = "icCoolingRegular";
        private const string IconOnline = "icRemoteButtonGreen";
        private const string IconOffline = "icRemoteButtonRed";

        // Tiempos para no exceder el límite de peticiones de Sensibo
        private const int SetpointDebounceMs = 1500;   // espera a que el usuario suelte el dial
        private const int CommandDebounceMs = 300;     // agrupa toques rápidos de botones
        private const int DefaultBackoffSeconds = 60;  // pausa tras un 429
        private const int RetryAfterErrorMs = 5000;

        // Rango amplio para cubrir °C (16-30) y °F (61-86); el driver limita según la unidad activa
        private const int SetpointMinAny = 10;
        private const int SetpointMaxAny = 90;

        #endregion

        #region Campos

        private PropertyValue<string> _tileStatus;
        private PropertyValue<string> _tileIcon;
        private PropertyValue<string> _tileSecondaryIcon;
        private PropertyValue<string> _statusText;
        private PropertyValue<bool> _power;
        private PropertyValue<string> _powerLabel;
        private PropertyValue<string> _mode;
        private PropertyValue<string> _fan;
        private PropertyValue<string> _humidity;
        private PropertyValue<bool> _heatEnabled;
        private PropertyValue<bool> _coolEnabled;
        private PropertyValue<bool> _autoEnabled;
        private PropertyValue<string> _units;
        private PropertyValue<int> _setpointAuto;
        private PropertyValue<int> _setpointCool;
        private PropertyValue<int> _setpointHeat;
        private PropertyValue<string> _modeIcon;
        private PropertyValue<int> _currentTemp;
        private PropertyValue<string> _currentTempLabel;

        private readonly object _sync = new object();
        private readonly Dictionary<string, PropertyAvailableValue<string>> _modeValues = new Dictionary<string, PropertyAvailableValue<string>>();
        private readonly Dictionary<string, PropertyAvailableValue<string>> _fanValues = new Dictionary<string, PropertyAvailableValue<string>>();
        private bool _capabilitiesLoaded;
        private SensiboRemoteCapabilities _capabilities;
        private string _apiKey = string.Empty;
        private string _podIdSetting = string.Empty;   // valor del atributo de usuario
        private string _podId = string.Empty;          // pod activo (el configurado o el primero de la cuenta)
        private int _pollSeconds = 60;
        private string _unit = "C";
        private bool _polling;
        private bool _initialized;
        private SensiboApiClient _api;
        private CTimer _pollTimer;
        private CTimer _flushTimer;
        private bool _flushing;
        private bool _flushAgain;
        private bool _disposed;
        private DateTime _backoffUntil = DateTime.MinValue;

        // Contadores para descartar respuestas viejas: cambian al reconfigurar el driver y con cada escritura
        private int _configGeneration;
        private int _writeGeneration;

        // Cambios pendientes de enviar: propiedad de Sensibo -> valor (el último gana)
        private readonly Dictionary<string, object> _pending = new Dictionary<string, object>();

        // Orden de envío: el modo antes que el setpoint (Sensibo valida la temperatura contra el modo activo)
        private static readonly string[] SendOrder = new[] { "mode", "fanLevel", "targetTemperature" };

        #endregion

        #region Constructor

        public SensiboThermostat()
        {
            AddUserAttributes();
            CreateDeviceDefinition();

            // El protocolo recibe los atributos de usuario (ABasicDriver.SetUserAttribute se los reenvía)
            ConnectionTransport = new SensiboTransport();
            DeviceProtocol = new SensiboProtocol(ConnectionTransport, Id, OnUserAttribute);
        }

        private void AddUserAttributes()
        {
            AddUserAttribute(UserAttributeType.Custom, AttrApiKey, "Sensibo API Key",
                "API key generada en home.sensibo.com/me/api", true,
                UserAttributeRequiredForConnectionType.Before, UserAttributeDataType.String, string.Empty);

            AddUserAttribute(UserAttributeType.Custom, AttrPodId, "Sensibo Pod ID (opcional)",
                "ID del dispositivo. Vacío = primer Sensibo de la cuenta", true,
                UserAttributeRequiredForConnectionType.None, UserAttributeDataType.String, string.Empty);

            AddUserAttribute(UserAttributeType.Custom, AttrPollSeconds, "Intervalo de consulta (s)",
                "Segundos entre consultas a Sensibo (mínimo 30)", true,
                UserAttributeRequiredForConnectionType.None, UserAttributeDataType.Number, "60");
        }

        private void CreateDeviceDefinition()
        {
            _tileStatus = CreateProperty<string>(new PropertyDefinition(PTileStatus, PTileStatus, DevicePropertyType.String));
            _tileIcon = CreateProperty<string>(new PropertyDefinition(PTileIcon, PTileIcon, DevicePropertyType.String));
            _tileSecondaryIcon = CreateProperty<string>(new PropertyDefinition(PTileSecondaryIcon, PTileSecondaryIcon, DevicePropertyType.String));
            _statusText = CreateProperty<string>(new PropertyDefinition(PStatusText, PStatusText, DevicePropertyType.String));

            _power = CreateProperty<bool>(new PropertyDefinition(PPower, PPower, DevicePropertyType.Boolean));
            _powerLabel = CreateProperty<string>(new PropertyDefinition(PPowerLabel, PPowerLabel, DevicePropertyType.String));

            // Constructor del SDK: (valor, tipo, clave de traducción SIN "^", etiqueta de respaldo)
            var modes = new List<IPropertyAvailableValue>();
            foreach (var m in SensiboModes.All)
            {
                var av = new PropertyAvailableValue<string>(m, DevicePropertyType.String, "mode_" + m, FallbackLabel(m));
                _modeValues[m] = av;
                modes.Add(av);
            }
            _mode = CreateProperty<string>(new PropertyDefinition(PMode, PMode, DevicePropertyType.String, modes));

            var fans = new List<IPropertyAvailableValue>();
            foreach (var f in SensiboFanLevels.All)
            {
                var av = new PropertyAvailableValue<string>(f, DevicePropertyType.String, "fan_" + f, FallbackLabel(f));
                _fanValues[f] = av;
                fans.Add(av);
            }
            _fan = CreateProperty<string>(new PropertyDefinition(PFan, PFan, DevicePropertyType.String, fans));

            _humidity = CreateProperty<string>(new PropertyDefinition(PHumidity, PHumidity, DevicePropertyType.String));

            // Control "thermostat" de la UI de extensiones
            _heatEnabled = CreateProperty<bool>(new PropertyDefinition(PHeatEnabled, PHeatEnabled, DevicePropertyType.Boolean));
            _coolEnabled = CreateProperty<bool>(new PropertyDefinition(PCoolEnabled, PCoolEnabled, DevicePropertyType.Boolean));
            _autoEnabled = CreateProperty<bool>(new PropertyDefinition(PAutoEnabled, PAutoEnabled, DevicePropertyType.Boolean));
            _units = CreateProperty<string>(new PropertyDefinition(PUnits, PUnits, DevicePropertyType.String));
            _setpointAuto = CreateProperty<int>(new PropertyDefinition(PSetpointAuto, PSetpointAuto, DevicePropertyType.Int32, SetpointMinAny, SetpointMaxAny, 1));
            _setpointCool = CreateProperty<int>(new PropertyDefinition(PSetpointCool, PSetpointCool, DevicePropertyType.Int32, SetpointMinAny, SetpointMaxAny, 1));
            _setpointHeat = CreateProperty<int>(new PropertyDefinition(PSetpointHeat, PSetpointHeat, DevicePropertyType.Int32, SetpointMinAny, SetpointMaxAny, 1));
            _modeIcon = CreateProperty<string>(new PropertyDefinition(PModeIcon, PModeIcon, DevicePropertyType.String));
            _currentTemp = CreateProperty<int>(new PropertyDefinition(PCurrentTemp, PCurrentTemp, DevicePropertyType.Int32));
            _currentTempLabel = CreateProperty<string>(new PropertyDefinition(PCurrentTempLabel, PCurrentTempLabel, DevicePropertyType.String));

            // Valores iniciales
            _tileStatus.Value = "^statusConnecting";
            _tileIcon.Value = IconClimate;
            _tileSecondaryIcon.Value = IconOffline;
            _statusText.Value = "^statusConnecting";
            _powerLabel.Value = "^off";
            _mode.Value = "cool";
            _fan.Value = "auto";
            _humidity.Value = "--";
            _units.Value = "Celsius";
            _setpointAuto.Value = 24;
            _setpointCool.Value = 24;
            _setpointHeat.Value = 24;
            _modeIcon.Value = IconCooling;
            _currentTempLabel.Value = "--";
            ApplyModeFlags("cool");
        }

        #endregion

        #region Ciclo de vida

        public void Initialize()
        {
            // ICloudConnected: se llama cuando el sistema tiene acceso a internet
            DeviceProtocol.Initialize(DriverData);
            _initialized = true;
            StartIfConfigured();
        }

        public override void Connect()
        {
            StartIfConfigured();
        }

        private void OnUserAttribute(string attributeId, string value)
        {
            var v = value == null ? string.Empty : value.Trim();
            var changed = false;
            SensiboApiClient oldApi = null;
            switch (attributeId)
            {
                case AttrApiKey:
                    lock (_sync)
                    {
                        if (v != _apiKey)
                        {
                            _apiKey = v;
                            oldApi = _api;
                            _api = null;
                            ResetDeviceState();
                            changed = true;
                        }
                    }
                    break;
                case AttrPodId:
                    lock (_sync)
                    {
                        if (v != _podIdSetting)
                        {
                            _podIdSetting = v;
                            ResetDeviceState();
                            changed = true;
                        }
                    }
                    break;
                case AttrPollSeconds:
                    int seconds;
                    if (int.TryParse(v, out seconds))
                        _pollSeconds = Math.Max(30, seconds);
                    break;
            }

            // Fuera del lock: espera a que termine una petición en curso del cliente anterior
            if (oldApi != null) oldApi.Dispose();
            if (changed && _initialized)
            {
                ApplyModeFlags(_mode.Value);
                Commit();
            }

            if (_initialized) StartIfConfigured();
        }

        /// <summary>
        /// Olvida todo lo ligado al equipo anterior (pod, capacidades, cambios sin enviar).
        /// Se llama con _sync tomado al cambiar la API key o el Pod ID.
        /// </summary>
        private void ResetDeviceState()
        {
            _podId = _podIdSetting;
            _capabilities = null;
            _capabilitiesLoaded = false;
            _pending.Clear();
            _configGeneration++;
            foreach (var kv in _modeValues) kv.Value.Enabled = true;
            foreach (var kv in _fanValues) kv.Value.Enabled = true;
        }

        private void StartIfConfigured()
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                SetStatus(false, "^statusNeedsApiKey");
                return;
            }

            if (_pollTimer == null)
                _pollTimer = new CTimer(PollCallback, null, 0, _pollSeconds * 1000);
            else
                _pollTimer.Reset(0, _pollSeconds * 1000);
        }

        public override void Dispose()
        {
            lock (_sync) _disposed = true;
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer.Dispose();
                _pollTimer = null;
            }
            if (_flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
                _flushTimer = null;
            }
            DisposeApi();
            base.Dispose();
        }

        private void DisposeApi()
        {
            SensiboApiClient api;
            lock (_sync)
            {
                api = _api;
                _api = null;
            }
            if (api != null) api.Dispose();
        }

        private SensiboApiClient Api
        {
            get
            {
                lock (_sync)
                {
                    if (_api == null && !_disposed && !string.IsNullOrEmpty(_apiKey))
                        _api = new SensiboApiClient(_apiKey);
                    return _api;
                }
            }
        }

        #endregion

        #region Lectura de estado

        private void PollCallback(object state)
        {
            Refresh();
        }

        /// <summary>Pide una consulta inmediata en el hilo del temporizador, sin bloquear al que llama (p. ej. la UI).</summary>
        private void RequestRefresh()
        {
            if (_pollTimer != null)
                _pollTimer.Reset(0, _pollSeconds * 1000);
            else
                StartIfConfigured();
        }

        private void Refresh()
        {
            int configGen;
            string podId;
            lock (_sync)
            {
                if (_polling) return;
                _polling = true;
                configGen = _configGeneration;
                podId = _podId;
            }

            try
            {
                var api = Api;
                if (api == null) return;
                if (InBackoff()) return;

                if (string.IsNullOrEmpty(podId))
                {
                    var pods = api.GetPods();
                    if (pods == null || pods.Count == 0)
                    {
                        SetStatus(false, "^statusNoDevices");
                        return;
                    }
                    lock (_sync)
                    {
                        if (configGen != _configGeneration) return;
                        _podId = podId = pods[0].Id;
                    }
                    Log("Sensibo: PodId not set, using " + podId);
                }

                if (!_capabilitiesLoaded)
                    LoadCapabilities(api, podId, configGen);

                int writeGen;
                lock (_sync) writeGen = _writeGeneration;

                var pod = api.GetPod(podId);
                ApplyPod(pod, configGen, writeGen);
            }
            catch (SensiboApiException ex)
            {
                if (ex.StatusCode == 429)
                {
                    StartBackoff();
                    return;
                }
                ErrorLog.Error("Sensibo: poll failed: " + ex.Message);
                string status;
                if (ex.StatusCode == 401 || ex.StatusCode == 403) status = "^statusBadApiKey";
                else if (ex.StatusCode == 404) status = "^statusPodNotFound";
                else status = "^statusOffline";
                SetStatus(false, status);
            }
            catch (Exception ex)
            {
                ErrorLog.Error("Sensibo: poll failed: " + ex.Message);
                SetStatus(false, "^statusOffline");
            }
            finally
            {
                lock (_sync) _polling = false;
            }
        }

        private void ApplyPod(SensiboPod pod, int configGen, int writeGen)
        {
            if (pod == null) return;

            var alive = pod.ConnectionStatus == null || pod.ConnectionStatus.IsAlive;
            var ac = pod.AcState;

            bool stale;
            lock (_sync)
            {
                // La respuesta es de un equipo o una API key que ya no están configurados
                if (configGen != _configGeneration) return;
                // Hay cambios sin enviar, o se envió alguno mientras se consultaba:
                // el acState recibido puede ser anterior a lo que muestra la UI
                stale = _pending.Count > 0 || writeGen != _writeGeneration;
            }

            if (ac != null && !stale)
            {
                _unit = ac.TemperatureUnit == "F" ? "F" : "C";
                _units.Value = _unit == "F" ? "Fahrenheit" : "Celsius";

                _power.Value = ac.On;
                _powerLabel.Value = ac.On ? "^on" : "^off";

                if (!string.IsNullOrEmpty(ac.Mode)) _mode.Value = ac.Mode;
                if (!string.IsNullOrEmpty(ac.FanLevel)) _fan.Value = ac.FanLevel;
                ApplyModeFlags(ac.Mode);

                if (ac.TargetTemperature.HasValue)
                    SetAllSetpoints(ac.TargetTemperature.Value);
            }

            if (pod.Measurements != null)
            {
                if (pod.Measurements.Temperature.HasValue)
                {
                    // Sensibo reporta las mediciones siempre en °C
                    var t = pod.Measurements.Temperature.Value;
                    if (_unit == "F") t = t * 9.0 / 5.0 + 32.0;
                    _currentTemp.Value = (int)Math.Round(t);
                    _currentTempLabel.Value = t.ToString("0.0") + "°";
                }
                if (pod.Measurements.Humidity.HasValue)
                    _humidity.Value = pod.Measurements.Humidity.Value.ToString("0") + "%";
            }

            var roomName = pod.Room != null && !string.IsNullOrEmpty(pod.Room.Name) ? pod.Room.Name : "Sensibo";
            Connected = alive;
            _tileSecondaryIcon.Value = alive ? IconOnline : IconOffline;
            _statusText.Value = alive ? roomName : "^statusOffline";
            UpdateTile();
            Commit();
        }

        /// <summary>
        /// Ajusta el control de termostato y las velocidades disponibles al modo activo.
        /// heat y auto usan su propio setpoint; los demás modos con temperatura (cool, dry y, en algunos
        /// equipos, fan) usan el de frío. Un modo sin temperatura no muestra setpoint.
        /// </summary>
        private void ApplyModeFlags(string mode)
        {
            var hasTemperature = ModeHasTemperature(mode);
            _heatEnabled.Value = mode == "heat";
            _autoEnabled.Value = mode == "auto";
            _coolEnabled.Value = hasTemperature && mode != "heat" && mode != "auto";
            _modeIcon.Value = mode == "cool" || mode == "dry" ? IconCooling : IconClimate;

            var modeCaps = GetModeCapabilities(mode);
            var fanLevels = modeCaps != null ? modeCaps.FanLevels : null;
            foreach (var kv in _fanValues)
                kv.Value.Enabled = fanLevels == null || fanLevels.Count == 0 || fanLevels.Contains(kv.Key);
        }

        private SensiboModeCapabilities GetModeCapabilities(string mode)
        {
            var caps = _capabilities;
            SensiboModeCapabilities modeCaps;
            if (caps == null || caps.Modes == null || mode == null || !caps.Modes.TryGetValue(mode, out modeCaps))
                return null;
            return modeCaps;
        }

        /// <summary>Sin capacidades conocidas se asume que todos los modos menos fan aceptan temperatura.</summary>
        private bool ModeHasTemperature(string mode)
        {
            var modeCaps = GetModeCapabilities(mode);
            if (modeCaps == null) return mode != "fan";
            return modeCaps.Temperatures != null && modeCaps.Temperatures.Count > 0;
        }

        /// <summary>Rango de setpoint del modo en la unidad activa; si el equipo no lo informa, 16-30 °C / 61-86 °F.</summary>
        private void GetSetpointRange(string mode, out int min, out int max)
        {
            min = _unit == "F" ? 61 : 16;
            max = _unit == "F" ? 86 : 30;

            var modeCaps = GetModeCapabilities(mode);
            SensiboTemperatureRange range;
            if (modeCaps == null || modeCaps.Temperatures == null ||
                !modeCaps.Temperatures.TryGetValue(_unit, out range) ||
                range == null || range.Values == null || range.Values.Count == 0)
                return;

            min = int.MaxValue;
            max = int.MinValue;
            foreach (var v in range.Values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        private void SetAllSetpoints(int value)
        {
            _setpointAuto.Value = value;
            _setpointCool.Value = value;
            _setpointHeat.Value = value;
        }

        private void UpdateTile()
        {
            if (!_power.Value)
                _tileStatus.Value = "^off";
            else
                _tileStatus.Value = _currentTempLabel.Value + " → " + _setpointCool.Value + "°";
        }

        private void SetStatus(bool online, string text)
        {
            Connected = online;
            _tileSecondaryIcon.Value = online ? IconOnline : IconOffline;
            _statusText.Value = text;
            if (!online) _tileStatus.Value = text;
            Commit();
        }

        #endregion

        #region Entrada desde la UI

        protected override IOperationResult DoCommand(string command, string[] parameters)
        {
            try
            {
                switch (command)
                {
                    case CmdTogglePower:
                        SetPower(!_power.Value);
                        break;
                    case CmdRefresh:
                        RequestRefresh();
                        break;
                    default:
                        ErrorLog.Warn("Sensibo: unhandled command: " + command);
                        return new OperationResult(OperationResultCode.Error, "Unhandled command");
                }
                return new OperationResult(OperationResultCode.Success);
            }
            catch (Exception ex)
            {
                ErrorLog.Error("Sensibo: command " + command + " failed: " + ex.Message);
                return new OperationResult(OperationResultCode.Error, ex.Message);
            }
        }

        protected override IOperationResult SetDriverPropertyValue<T>(string propertyKey, T value)
        {
            try
            {
                switch (propertyKey)
                {
                    case PPower:
                        SetPower(Convert.ToBoolean(value));
                        break;
                    case PMode:
                        SetMode(Convert.ToString(value));
                        break;
                    case PFan:
                        SetFanLevel(Convert.ToString(value));
                        break;
                    case PSetpointAuto:
                    case PSetpointCool:
                    case PSetpointHeat:
                        SetSetpoint(Convert.ToInt32(value));
                        break;
                    case PHeatEnabled:
                        if (Convert.ToBoolean(value)) SetMode("heat");
                        break;
                    case PCoolEnabled:
                        if (Convert.ToBoolean(value)) SetMode("cool");
                        break;
                    case PAutoEnabled:
                        if (Convert.ToBoolean(value)) SetMode("auto");
                        break;
                    default:
                        return new OperationResult(OperationResultCode.Error, "Read-only property");
                }
                return new OperationResult(OperationResultCode.Success);
            }
            catch (Exception ex)
            {
                ErrorLog.Error("Sensibo: write " + propertyKey + " failed: " + ex.Message);
                return new OperationResult(OperationResultCode.Error, ex.Message);
            }
        }

        protected override IOperationResult SetDriverPropertyValue<T>(string objectId, string propertyKey, T value)
        {
            return new OperationResult(OperationResultCode.Success);
        }

        #endregion

        #region Operaciones (también disponibles en secuencias de Crestron Home)

        [ProgrammableOperation("^opPowerOn")]
        public void PowerOn()
        {
            SetPower(true);
        }

        [ProgrammableOperation("^opPowerOff")]
        public void PowerOff()
        {
            SetPower(false);
        }

        [ProgrammableOperation("^opSetMode")]
        public void SetMode(
            [Display("^mode")]
            [AvailableValues("cool", "heat", "fan", "dry", "auto")]
            string mode)
        {
            if (SensiboModes.All.IndexOf(mode) < 0)
                throw new ArgumentException("Invalid mode: " + mode);

            Queue("mode", mode, CommandDebounceMs);
            _mode.Value = mode;
            ApplyModeFlags(mode);
            UpdateTile();
            Commit();
        }

        [ProgrammableOperation("^opSetFan")]
        public void SetFanLevel(
            [Display("^fan")]
            [AvailableValues("auto", "quiet", "low", "medium_low", "medium", "medium_high", "high", "strong")]
            string level)
        {
            if (SensiboFanLevels.All.IndexOf(level) < 0)
                throw new ArgumentException("Invalid fan level: " + level);
            Queue("fanLevel", level, CommandDebounceMs);
            _fan.Value = level;
            Commit();
        }

        [ProgrammableOperation("^opSetSetpoint")]
        public void SetSetpoint(
            [Display("^setpoint")]
            [Min(16)]
            [Max(86)]
            int setpoint)
        {
            var mode = _mode.Value;
            if (!ModeHasTemperature(mode))
            {
                ErrorLog.Warn("Sensibo: mode " + mode + " has no setpoint, ignoring " + setpoint);
                return;
            }

            int min, max;
            GetSetpointRange(mode, out min, out max);
            var sp = Math.Min(max, Math.Max(min, setpoint));

            Queue("targetTemperature", sp, SetpointDebounceMs);
            SetAllSetpoints(sp);
            UpdateTile();
            Commit();
        }

        private void SetPower(bool on)
        {
            Queue("on", on, CommandDebounceMs);
            _power.Value = on;
            _powerLabel.Value = on ? "^on" : "^off";
            UpdateTile();
            Commit();
        }

        #endregion

        #region Cola de envío y control de límite

        /// <summary>
        /// Guarda el cambio y programa el envío. Varios cambios seguidos de la misma propiedad
        /// (p. ej. girar el dial) se colapsan en una sola petición con el último valor.
        /// </summary>
        private void Queue(string sensiboProperty, object value, int delayMs)
        {
            if (Api == null)
            {
                ErrorLog.Warn("Sensibo: ignoring " + sensiboProperty + ", driver not configured");
                return;
            }

            lock (_sync)
            {
                _pending[sensiboProperty] = value;
                _writeGeneration++;
            }
            ScheduleFlush(delayMs);
        }

        /// <summary>
        /// Siguiente propiedad a enviar (con _sync tomado). Apagar va primero; encender va al final,
        /// para que el equipo arranque ya con el modo, la velocidad y el setpoint nuevos.
        /// </summary>
        private string NextPendingProperty()
        {
            object on;
            var hasOn = _pending.TryGetValue("on", out on);
            if (hasOn && !Convert.ToBoolean(on)) return "on";

            foreach (var p in SendOrder)
                if (_pending.ContainsKey(p)) return p;

            if (hasOn) return "on";

            foreach (var kv in _pending)
                return kv.Key;
            return null;
        }

        private void ScheduleFlush(int delayMs)
        {
            var wait = Math.Max(delayMs, BackoffRemainingMs());
            lock (_sync)
            {
                if (_disposed) return;
                if (_flushTimer == null)
                    _flushTimer = new CTimer(FlushCallback, null, wait);
                else
                    _flushTimer.Reset(wait);
            }
        }

        private void FlushCallback(object state)
        {
            lock (_sync)
            {
                if (_flushing)
                {
                    // El envío en curso ya consumió este disparo: se reprograma al terminar
                    _flushAgain = true;
                    return;
                }
                _flushing = true;
            }

            var sentAny = false;
            try
            {
                while (true)
                {
                    if (InBackoff())
                    {
                        ScheduleFlush(0);
                        return;
                    }

                    string property;
                    object value = null;
                    string podId;
                    lock (_sync)
                    {
                        property = NextPendingProperty();
                        if (property != null) value = _pending[property];
                        podId = _podId;
                    }
                    if (property == null) break;

                    var api = Api;
                    if (api == null || string.IsNullOrEmpty(podId))
                    {
                        lock (_sync) _pending.Clear();
                        break;
                    }

                    try
                    {
                        api.SetAcProperty(podId, property, value);
                        sentAny = true;
                        lock (_sync)
                        {
                            _writeGeneration++;
                            // Solo se borra si nadie lo cambió mientras se enviaba
                            object current;
                            if (_pending.TryGetValue(property, out current) && Equals(current, value))
                                _pending.Remove(property);
                        }
                    }
                    catch (SensiboApiException ex)
                    {
                        if (ex.StatusCode == 429)
                        {
                            StartBackoff();
                            ScheduleFlush(0);
                            return;
                        }
                        if (ex.IsTransient)
                        {
                            // Error de red o del servidor: se conserva el cambio y se reintenta
                            ErrorLog.Error("Sensibo: write " + property + " failed, retrying: " + ex.Message);
                            ScheduleFlush(RetryAfterErrorMs);
                            return;
                        }
                        // Error definitivo (p. ej. valor no soportado por el equipo): se descarta y se resincroniza la UI
                        ErrorLog.Error("Sensibo: write " + property + " failed: " + ex.Message);
                        lock (_sync) _pending.Remove(property);
                        ScheduleRefresh(RetryAfterErrorMs);
                    }
                    catch (Exception ex)
                    {
                        // Error inesperado: se conserva el cambio y se reintenta
                        ErrorLog.Error("Sensibo: write " + property + " failed, retrying: " + ex.Message);
                        ScheduleFlush(RetryAfterErrorMs);
                        return;
                    }
                }

                // El estado ya es conocido: la siguiente consulta puede esperar un intervalo completo
                if (sentAny) ScheduleRefresh(_pollSeconds * 1000);
            }
            finally
            {
                bool again;
                lock (_sync)
                {
                    _flushing = false;
                    again = _flushAgain && _pending.Count > 0;
                    _flushAgain = false;
                }
                if (again) ScheduleFlush(0);
            }
        }

        private void ScheduleRefresh(int delayMs)
        {
            if (_pollTimer != null)
                _pollTimer.Reset(delayMs, _pollSeconds * 1000);
        }

        private void StartBackoff()
        {
            lock (_sync)
            {
                _backoffUntil = DateTime.Now.AddSeconds(DefaultBackoffSeconds);
            }
            ErrorLog.Warn("Sensibo: rate limit reached (429), pausing requests for " + DefaultBackoffSeconds + " s");
        }

        private bool InBackoff()
        {
            return BackoffRemainingMs() > 0;
        }

        private int BackoffRemainingMs()
        {
            lock (_sync)
            {
                var ms = (_backoffUntil - DateTime.Now).TotalMilliseconds;
                return ms > 0 ? (int)ms : 0;
            }
        }

        #endregion

        /// <summary>
        /// Guarda las capacidades del AC (modos, velocidades y rangos de temperatura por modo) y deshabilita en la UI lo que no soporta.
        /// Si falla, todas las opciones quedan habilitadas (Sensibo rechazará las no soportadas y el driver resincroniza).
        /// </summary>
        private void LoadCapabilities(SensiboApiClient api, string podId, int configGen)
        {
            try
            {
                var caps = api.GetCapabilities(podId);
                lock (_sync)
                {
                    if (configGen != _configGeneration) return;
                    _capabilitiesLoaded = true;
                    if (caps == null || caps.Modes == null || caps.Modes.Count == 0) return;
                    _capabilities = caps;
                }

                foreach (var kv in _modeValues)
                    kv.Value.Enabled = caps.Modes.ContainsKey(kv.Key);
                ApplyModeFlags(_mode.Value);
                Commit();
            }
            catch (SensiboApiException ex)
            {
                // 429 y errores de red se manejan en Refresh; las capacidades se vuelven a pedir en la siguiente consulta
                if (ex.StatusCode == 429 || ex.IsTransient) throw;
                ErrorLog.Warn("Sensibo: could not read capabilities: " + ex.Message);
                lock (_sync)
                {
                    if (configGen == _configGeneration) _capabilitiesLoaded = true;
                }
            }
        }

        private static string FallbackLabel(string value)
        {
            switch (value)
            {
                case "cool": return "Cool";
                case "heat": return "Heat";
                case "fan": return "Fan";
                case "dry": return "Dry";
                case "auto": return "Auto";
                case "quiet": return "Quiet";
                case "low": return "Low";
                case "medium_low": return "Medium-Low";
                case "medium": return "Medium";
                case "medium_high": return "Medium-High";
                case "high": return "High";
                case "strong": return "Strong";
                default: return value;
            }
        }
    }
}
