using System;
using Crestron.RAD.Common.BasicDriver;
using Crestron.RAD.Common.Transports;

namespace SensiboCrestronHome
{
    /// <summary>
    /// En el SDK, ABasicDriver.SetUserAttribute no es virtual: reenvía los valores al protocolo.
    /// Este protocolo solo los pasa de vuelta al driver.
    /// </summary>
    public class SensiboProtocol : ABaseDriverProtocol
    {
        private readonly Action<string, string> _onAttribute;

        public SensiboProtocol(ISerialTransport transport, byte id, Action<string, string> onAttribute)
            : base(transport, id)
        {
            _onAttribute = onAttribute;
        }

        protected override void ConnectionChangedEvent(bool connection)
        {
        }

        protected override void ChooseDeconstructMethod(ValidatedRxData validatedData)
        {
        }

        public override void SetUserAttribute(string attributeId, string attributeValue)
        {
            if (_onAttribute != null) _onAttribute(attributeId, attributeValue);
        }

        public override void SetUserAttribute(string attributeId, bool attributeValue)
        {
            if (_onAttribute != null) _onAttribute(attributeId, attributeValue ? "true" : "false");
        }

        public override void SetUserAttribute(string attributeId, ushort attributeValue)
        {
            if (_onAttribute != null) _onAttribute(attributeId, attributeValue.ToString());
        }
    }
}
