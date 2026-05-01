using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Cargo
{
    [DataDefinition, NetSerializable, Serializable]
    public sealed partial record class CargoOrderItemData
    {

        [DataField]
        public ProtoId<CargoProductPrototype> Product;

        [DataField]
        public int Quantity;
        [DataField]
        public bool WithContainer = true;

        [DataField]
        public bool ToBeOrdered = true;
        [DataField]
        public int NumOrdered = 0;

        public CargoOrderItemData(ProtoId<CargoProductPrototype> product, int quantity)
        {
            Product = product;
            Quantity = quantity;
        }
    }
}
