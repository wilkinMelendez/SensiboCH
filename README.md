# Sensibo para Crestron Home

![Build](https://github.com/wilkinMelendez/SensiboCH/actions/workflows/build.yml/badge.svg)
![Plataforma](https://img.shields.io/badge/plataforma-Crestron%20Home-1f6feb)
![Procesadores](https://img.shields.io/badge/procesadores-4--Series-555)
![Framework](https://img.shields.io/badge/.NET%20Framework-4.7-512bd4)
![SDK](https://img.shields.io/badge/Crestron%20Drivers%20SDK-29.0.10-orange)
![Versión](https://img.shields.io/badge/versi%C3%B3n-1.0000.0004-success)

Driver de tipo **Extension Device** para **Crestron Home** que controla aires acondicionados a través de los controladores **Sensibo Sky**, **Sensibo Air** y **Sensibo Air Pro**, usando la [API cloud v2 de Sensibo](https://sensibo.github.io/).

No requiere hardware adicional ni integración local: el procesador habla directamente con la nube de Sensibo por HTTPS.

---

## Contenido

- [Características](#características)
- [Requisitos](#requisitos)
- [Instalación](#instalación)
- [Configuración](#configuración)
- [Interfaz de usuario](#interfaz-de-usuario)
- [Secuencias y operaciones programables](#secuencias-y-operaciones-programables)
- [Funcionamiento interno](#funcionamiento-interno)
- [Compilación](#compilación)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Solución de problemas](#solución-de-problemas)
- [Historial de versiones](#historial-de-versiones)

---

## Características

- **Control completo del aire acondicionado**: encendido y apagado, modo (`cool`, `heat`, `fan`, `dry`, `auto`), velocidad del ventilador y temperatura objetivo.
- **Control de termostato nativo** de Crestron Home, con temperatura ambiente, humedad y setpoint.
- **Adaptación automática al equipo**: lee las capacidades del control remoto del AC y solo habilita los modos, velocidades y rangos de temperatura que soporta cada modo.
- **Celsius y Fahrenheit**: sigue la unidad configurada en la cuenta de Sensibo.
- **Uso eficiente de la API**:
  - Agrupa los cambios rápidos (por ejemplo, al girar el dial) en una sola petición.
  - Pide las respuestas comprimidas con gzip, lo que da un límite de peticiones más alto.
  - Pausa automáticamente al recibir un `429 Too Many Requests`.
- **Envío ordenado y confiable**: los cambios se envían en un orden coherente (apagar primero, encender al final) y se reintentan ante errores de red.
- **Conexión segura**: valida el certificado TLS de Sensibo y nunca escribe la API key en el log.
- **Operaciones programables** disponibles en las secuencias y escenas de Crestron Home.
- **Interfaz en inglés y español** (`en-US`, `es-MX`).

## Requisitos

| Componente | Requisito |
|---|---|
| Procesador | Crestron 4-Series con Crestron Home (CP4-R, MC4-R, PC4-R, etc.) |
| Conectividad | Acceso a internet desde el procesador hacia `home.sensibo.com:443` |
| Dispositivo | Sensibo Sky, Sensibo Air o Sensibo Air Pro, ya configurado en la app de Sensibo |
| Cuenta | API key de Sensibo |

## Instalación

1. Descarga o [compila](#compilación) el paquete `ExtensionDevice_Sensibo_Thermostat_Cloud.pkg`.
2. En **Crestron Home Setup**, ve a **Drivers** y carga el `.pkg`.
3. Agrega el dispositivo **Sensibo Thermostat** a la habitación que corresponda.
4. Completa los parámetros descritos en [Configuración](#configuración).

> Si tienes varios aires acondicionados, agrega una instancia del driver por cada uno y configura su **Pod ID**.

## Configuración

| Parámetro | Obligatorio | Descripción |
|---|:---:|---|
| **Sensibo API Key** | Sí | Key generada en [home.sensibo.com/me/api](https://home.sensibo.com/me/api). |
| **Sensibo Pod ID** | No | ID del dispositivo. Si lo dejas vacío, se usa el primer Sensibo de la cuenta. |
| **Intervalo de consulta (s)** | No | Segundos entre consultas a Sensibo. Por defecto `60`, mínimo `30`. |

### Cómo obtener la API key

1. Inicia sesión en [home.sensibo.com](https://home.sensibo.com).
2. Abre [home.sensibo.com/me/api](https://home.sensibo.com/me/api).
3. Genera una key nueva y cópiala en el parámetro **Sensibo API Key**.

### Cómo obtener el Pod ID

Con la API key, consulta la lista de dispositivos de la cuenta:

```bash
curl "https://home.sensibo.com/api/v2/users/me/pods?fields=id,room&apiKey=TU_API_KEY"
```

Cada dispositivo aparece con su `id` y el nombre de su habitación (`room.name`).

> Trata la API key como una contraseña: da acceso completo a los equipos de la cuenta.

## Interfaz de usuario

**Tile de la habitación**
- Muestra el estado (`Apagado`, o la temperatura ambiente y el setpoint, por ejemplo `23.5° → 24°`) y un indicador de conexión.
- Al tocarlo, enciende o apaga el equipo. Su navegación abre la página de control.

**Página de control**
- **Termostato**: temperatura ambiente, setpoint y modo activo. El setpoint solo aparece en los modos que aceptan temperatura (por ejemplo, no en `fan` en la mayoría de los equipos).
- **Control**: encendido, selector de modo y selector de velocidad del ventilador.
- **Información**: humedad relativa y un botón para refrescar el estado.

## Secuencias y operaciones programables

| Operación | Parámetro | Valores |
|---|---|---|
| Encender | — | — |
| Apagar | — | — |
| Cambiar modo | Modo | `cool`, `heat`, `fan`, `dry`, `auto` |
| Cambiar velocidad | Velocidad | `auto`, `quiet`, `low`, `medium_low`, `medium`, `medium_high`, `high`, `strong` |
| Cambiar setpoint | Temperatura | 16–86. Se ajusta al rango que soporta el modo actual. |

Cada equipo soporta solo algunos modos y velocidades. Si se pide uno que el equipo no soporta, Sensibo lo rechaza y el driver vuelve a sincronizar el estado real.

## Funcionamiento interno

```
 Crestron Home UI ──► SensiboThermostat ──► cola de cambios ──► SensiboApiClient ──► API Sensibo v2
        ▲                    │              (debounce, orden,          (HTTPS + gzip)
        └──── propiedades ◄──┴── consulta periódica ◄─────────────────────────┘
```

- **Lectura**: un temporizador consulta el estado del pod (`acState`, mediciones y conexión) cada *N* segundos. Las capacidades del equipo se leen una sola vez por pod.
- **Escritura**:
  - Cada cambio se actualiza en la UI de inmediato y se guarda en una cola. Varios cambios a la misma propiedad se colapsan en uno con el último valor.
  - Los cambios se envían tras una espera corta (300 ms para los botones, 1,5 s para el setpoint).
  - Orden de envío: apagar primero, luego modo, velocidad y setpoint, y encender al final, para que el equipo arranque ya con la configuración nueva.
- **Consistencia**: si llega una lectura que se pidió antes de un cambio, el driver no la aplica sobre la UI. Al cambiar la API key o el Pod ID, se descarta todo lo ligado al equipo anterior.
- **Manejo de errores**:

  | Error | Comportamiento |
  |---|---|
  | `429` | Pausa todas las peticiones 60 s. |
  | Red o `5xx` | Conserva el cambio y lo reintenta a los 5 s. |
  | Otros `4xx` | Descarta el cambio y resincroniza la UI. |
  | `401` / `403` | Muestra *API key inválida*. |
  | `404` | Muestra *No se encontró el Sensibo*. |

## Compilación

Requisitos:
- [.NET SDK](https://dotnet.microsoft.com/download) 8 o superior (Windows, macOS o Linux).
- Acceso a nuget.org para descargar los paquetes de Crestron.

```bash
dotnet build -c Release
```

El paquete `ExtensionDevice_Sensibo_Thermostat_Cloud.pkg` se genera en `bin/Release/net47/`, listo para cargar en Crestron Home.

| Paquete NuGet | Uso |
|---|---|
| `Crestron.DeviceDrivers.DevKit` 29.0.10 | Framework de drivers (RAD) y Extension Devices |
| `Crestron.SimplSharp.SDK.Library` 2.22.40 | `CTimer`, `HttpsClient`, `ErrorLog`, Newtonsoft.Json |
| `Crestron.DeviceDrivers.ManifestUtil` 29.0.10 | Genera el `.dat` y el `.pkg` después del build |

> El código está escrito con sintaxis **C# 3** (`<LangVersion>3</LangVersion>`), por compatibilidad con la cadena de compilación de Crestron. No uses características de versiones más nuevas del lenguaje.

Cada push a `main` y cada pull request se compilan en GitHub Actions ([`build.yml`](.github/workflows/build.yml)). El `.pkg` resultante queda disponible como artefacto de la ejecución.

Para compilar sin generar el paquete:

```bash
dotnet build -c Release -p:GenerateDriverPackage=false
```

## Estructura del proyecto

```
.
├── SensiboThermostat.cs          # Driver: UI, consultas, comandos, cola de envío y límite de peticiones
├── SensiboApiClient.cs           # Cliente HTTPS para la API v2 de Sensibo
├── SensiboModels.cs              # Modelos JSON (pod, acState, mediciones, capacidades)
├── SensiboProtocol.cs            # Protocolo RAD que reenvía los atributos de usuario al driver
├── SensiboTransport.cs           # Transporte vacío que exige el framework RAD
├── ExtensionDevice_Sensibo_Thermostat_Cloud.json   # Manifiesto del driver (embebido en la DLL)
├── ExtensionDevice_Sensibo_Thermostat_Cloud.csproj
├── Properties/AssemblyInfo.cs
└── IncludeInPkg/
    ├── UiDefinitions/UiDefinition.xml   # Tile y página de control
    └── Translations/                    # en-US, es-MX
```

## Solución de problemas

| Estado en la UI | Causa probable | Solución |
|---|---|---|
| *Falta la API key* | El parámetro está vacío. | Ingresa la key en la configuración del driver. |
| *API key inválida* | Key incorrecta o revocada. | Genera una nueva en home.sensibo.com/me/api. |
| *No se encontraron dispositivos Sensibo* | La cuenta no tiene equipos. | Verifica la cuenta en la app de Sensibo. |
| *No se encontró el Sensibo* | El Pod ID no existe o pertenece a otra cuenta. | Revisa el Pod ID, o déjalo vacío para usar el primero de la cuenta. |
| *Sin conexión* | El procesador no llega a Sensibo, o el Sensibo está desconectado. | Revisa la salida a internet del procesador y el Wi-Fi del Sensibo. |
| No responde por un minuto | Se alcanzó el límite de peticiones (`429`). | Sube el intervalo de consulta; el driver reanuda solo. |

Los mensajes de diagnóstico se escriben en el log de errores del procesador con el prefijo `Sensibo:`. Para verlos, usa `err` desde la consola del procesador o la herramienta de log de Crestron Toolbox.

## Historial de versiones

### 1.0000.0004
- Valida el certificado TLS de Sensibo y quita la API key de los mensajes de error.
- Al cambiar el Pod ID o la API key, recarga las capacidades del equipo y descarta los cambios pendientes del anterior.
- El botón **Refrescar** ya no bloquea la interfaz.
- Los modos `dry` y `fan` muestran el setpoint según las capacidades reales del equipo, y las velocidades disponibles dependen del modo activo.
- El setpoint se limita al rango que soporta cada modo.
- Orden de envío determinista: apagar primero y encender al final.
- Las lecturas que llegan tarde ya no pisan cambios recientes en la UI.
- Los errores de red y `5xx` se reintentan en lugar de descartar el cambio.
- Nuevo estado *No se encontró el Sensibo* para un Pod ID inválido (`404`).
- `DeviceType` del manifiesto cambiado a `HVAC`.

### 1.0000.0003
- Versión inicial publicada.

---

*Sensibo es una marca de Sensibo Ltd. Crestron y Crestron Home son marcas de Crestron Electronics, Inc. Este proyecto no está afiliado ni respaldado por ninguna de las dos compañías.*
