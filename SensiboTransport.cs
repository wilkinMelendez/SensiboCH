using Crestron.RAD.Common.Transports;

namespace SensiboCrestronHome
{
    /// <summary>
    /// Transporte vacío: la comunicación real va por HTTPS en SensiboApiClient.
    /// El framework RAD requiere un transporte para crear el protocolo (mismo patrón que el ejemplo DoorLock del SDK).
    /// </summary>
    public class SensiboTransport : ATransportDriver
    {
        public SensiboTransport()
        {
            IsConnected = true;
        }

        public override void SendMethod(string message, object[] paramaters)
        {
        }

        public override void Start()
        {
        }

        public override void Stop()
        {
        }
    }
}
