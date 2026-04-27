using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Cargo
{
    [DataDefinition, NetSerializable, Serializable]
    public sealed partial class CargoOrderContainerData
    {

        /// <summary>
        /// The ID of the cargo product ordered.
        /// </summary>
        [DataField]
        public string Container;

        [DataField]
        public string ContainerID = string.Empty;

        [DataField]
        public int MaxItems = 30;

        /// <summary>
        /// The number of items in the order. Not readonly, as it might change
        /// due to caps on the amount of orders that can be placed.
        /// </summary>
        [DataField]
        public List<CargoOrderItemData> Products = new();

        [DataField]
        public string LableMessage = string.Empty;
        [DataField]
        public string LableName = string.Empty;
        [DataField]
        public bool IsSingleProduct = false;
        [DataField]
        public bool CrateRequired = false;
        public CargoOrderContainerData(string container, string containerID, CargoOrderItemData? item = null, bool crateRequired = false)
        {
            Container = container;
            ContainerID = containerID;
            CrateRequired = crateRequired;
            if (item == null)
            {
                return;
            }
            Products.Add(item);
            IsSingleProduct = true;
        }
    }
}
