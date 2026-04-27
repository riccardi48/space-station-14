using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Cargo
{
    [DataDefinition, NetSerializable, Serializable]
    public sealed partial class CargoOrderBasketData
    {

        /// <summary>
        /// The ID of the cargo product ordered.
        /// </summary>
        [DataField]
        public List<CargoOrderItemData> Products = new();
    }
}
