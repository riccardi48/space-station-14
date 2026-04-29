using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Cargo.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Database;
using Content.Shared.Emag.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Labels.Components;
using Content.Shared.Paper;
using Content.Shared.Station.Components;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.Random;
using System.ComponentModel;

namespace Content.Server.Cargo.Systems
{
    public sealed partial class CargoSystem
    {
        [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
        [Dependency] private readonly EmagSystem _emag = default!;
        [Dependency] private readonly IGameTiming _timing = default!;

        private void InitializeConsole()
        {
            SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleAddOrderMessage>(OnAddOrderMessage);
            SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleRemoveOrderMessage>(OnRemoveOrderMessage);
            SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleApproveOrderMessage>(OnApproveOrderMessage);
            SubscribeLocalEvent<CargoOrderConsoleComponent, BoundUIOpenedEvent>(OnOrderUIOpened);
            SubscribeLocalEvent<CargoOrderConsoleComponent, ComponentInit>(OnInit);
            SubscribeLocalEvent<CargoOrderConsoleComponent, InteractUsingEvent>(OnInteractUsing);
            SubscribeLocalEvent<CargoOrderConsoleComponent, GotEmaggedEvent>(OnEmagged);
        }

        private void OnInteractUsingCash(EntityUid uid, CargoOrderConsoleComponent component, ref InteractUsingEvent args)
        {
            var price = _pricing.GetPrice(args.Used);

            if (price == 0)
                return;

            var stationUid = _station.GetOwningStation(args.Used);

            if (!TryComp(stationUid, out StationBankAccountComponent? bank))
                return;

            _audio.PlayPvs(ApproveSound, uid);
            UpdateBankAccount((stationUid.Value, bank), (int) price, component.Account);
            QueueDel(args.Used);
            args.Handled = true;
        }
        private void OnInteractUsing(EntityUid uid, CargoOrderConsoleComponent component, ref InteractUsingEvent args)
        {
            if (HasComp<CashComponent>(args.Used))
            {
                OnInteractUsingCash(uid, component, ref args);
            }
            else if (TryComp<CargoSlipComponent>(args.Used, out var slip) && component.Mode == CargoOrderConsoleMode.DirectOrder)
            {
                return;
            }
        }

        private void OnInit(EntityUid uid, CargoOrderConsoleComponent orderConsole, ComponentInit args)
        {
            var station = _station.GetOwningStation(uid);
            UpdateOrderState(uid, station);
        }

        private void OnEmagged(Entity<CargoOrderConsoleComponent> ent, ref GotEmaggedEvent args)
        {
            if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
                return;

            if (_emag.CheckFlag(ent, EmagType.Interaction))
                return;

            args.Handled = true;
        }

        private void UpdateConsole()
        {
            var stationQuery = EntityQueryEnumerator<StationBankAccountComponent>();
            while (stationQuery.MoveNext(out var uid, out var bank))
            {
                if (Timing.CurTime < bank.NextIncomeTime)
                    continue;
                bank.NextIncomeTime += bank.IncomeDelay;

                var balanceToAdd = (int) Math.Round(bank.IncreasePerSecond * bank.IncomeDelay.TotalSeconds);
                UpdateBankAccount((uid, bank), balanceToAdd, bank.RevenueDistribution);
            }
        }

        #region Interface

        private void OnApproveOrderMessage(EntityUid uid, CargoOrderConsoleComponent component, CargoConsoleApproveOrderMessage args)
        {
            if (args.Actor is not { Valid: true } player)
                return;

            if (component.Mode != CargoOrderConsoleMode.DirectOrder)
                return;

            if (!_accessReaderSystem.IsAllowed(player, uid))
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-order-not-allowed"));
                PlayDenySound(uid, component);
                return;
            }

            var station = _station.GetOwningStation(uid);

            // No station to deduct from.
            if (!TryComp(station, out StationBankAccountComponent? bank) ||
                !TryComp(station, out StationDataComponent? stationData) ||
                !TryGetOrderDatabase(station, out var orderDatabase))
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-station-not-found"));
                PlayDenySound(uid, component);
                return;
            }

            // Find our order again. It might have been dispatched or approved already
            var order = orderDatabase.Orders[component.Account].Find(order => args.OrderId == order.OrderId && !order.Approved);
            if (order == null || !_protoMan.Resolve(order.Account, out var account))
            {
                return;
            }

            var availableProducts = GetAvailableProducts((uid, component));
            var cost = GetOrderCost(order);
            var accountBalance = GetBalanceFromAccount((station.Value, bank), order.Account);

            // Not enough balance
            if (cost > accountBalance)
            {
                ConsolePopup(args.Actor, Loc.GetString("cargo-console-insufficient-funds", ("cost", cost)));
                PlayDenySound(uid, component);
                return;
            }

            var ev = new FulfillCargoOrderEvent((station.Value, stationData), order, (uid, component));
            RaiseLocalEvent(ref ev);
            ev.FulfillmentEntity ??= station.Value;

            if (!ev.Handled)
            {
                ev.FulfillmentEntity = TryFulfillOrder((station.Value, stationData), order.Account, order, orderDatabase);

                if (ev.FulfillmentEntity == null)
                {
                    ConsolePopup(args.Actor, Loc.GetString("cargo-console-unfulfilled"));
                    PlayDenySound(uid, component);
                    return;
                }
            }

            order.Approved = true;
            _audio.PlayPvs(ApproveSound, uid);

            if (!_emag.CheckFlag(uid, EmagType.Interaction))
            {
                var tryGetIdentityShortInfoEvent = new TryGetIdentityShortInfoEvent(uid, player);
                RaiseLocalEvent(tryGetIdentityShortInfoEvent);
                order.SetApproverData(tryGetIdentityShortInfoEvent.Title);
                var message = Loc.GetString("cargo-console-unlock-approved-order-broadcast-header",
                    ("orderID", order.OrderId));
                message += "\n";
                foreach (var product in order.Basket)
                {
                    if (!_protoMan.TryIndex<CargoProductPrototype>(product.Product, out var productProto))
                        return;
                    message += Loc.GetString("cargo-console-unlock-approved-order-broadcast-item",
                    ("productName", Loc.GetString(productProto.Name)),
                    ("orderAmount", product.Quantity));
                    message += "\n";
                }
                message += Loc.GetString("cargo-console-unlock-approved-order-broadcast-footer",
                    ("approver", order.Approver ?? string.Empty),
                    ("cost", cost));
                _radio.SendRadioMessage(uid, message, account.RadioChannel, uid, escapeMarkup: false);
                if (CargoOrderConsoleComponent.BaseAnnouncementChannel != account.RadioChannel)
                    _radio.SendRadioMessage(uid, message, CargoOrderConsoleComponent.BaseAnnouncementChannel, uid, escapeMarkup: false);
            }

            ConsolePopup(args.Actor, Loc.GetString("cargo-console-trade-station", ("destination", MetaData(ev.FulfillmentEntity.Value).EntityName)));

            // Log order approval
            var adminString = "";
            foreach (var product in order.Basket)
            {
                adminString += $"{product.Quantity} {product.Product},";
            }

            _adminLogger.Add(LogType.Action,
                LogImpact.Low,
                $"{ToPrettyString(player):user} approved order [orderId:{order.OrderId}, products:{adminString}, requester:{order.Requester}, reason:{order.Reason}] on account {order.Account} with balance at {accountBalance}");

            UpdateBankAccount((station.Value, bank), -cost, order.Account);
            UpdateOrders(station.Value);
        }

        private EntityUid? TryFulfillOrder(Entity<StationDataComponent> stationData, ProtoId<CargoAccountPrototype> account, CargoOrderData order, StationCargoOrderDatabaseComponent orderDatabase)
        {
            var containers = SortOrders(order);
            return TryFulfillOrder(stationData, account, containers, orderDatabase);
        }

        private EntityUid? TryFulfillOrder(Entity<StationDataComponent> stationData, ProtoId<CargoAccountPrototype> account, List<CargoOrderContainerData> containers, StationCargoOrderDatabaseComponent orderDatabase)
        {

            // No slots at the trade station
            _listEnts.Clear();
            GetTradeStations(stationData, ref _listEnts);
            EntityUid? tradeDestination = null;

            // Try to fulfill from any station where possible, if the pad is not occupied.
            foreach (var trade in _listEnts)
            {
                var tradePads = GetCargoPallets(trade, BuySellType.Buy);
                _random.Shuffle(tradePads);

                var freePads = GetFreeCargoPallets(trade, tradePads);
                if (freePads.Count >= containers.Count) //check if the station has enough free pallets
                {
                    foreach (var pad in freePads)
                    {
                        var coordinates = new EntityCoordinates(trade, pad.Transform.LocalPosition);

                        if (FulfillOrder(containers[0], coordinates, orderDatabase.PrinterOutput))
                        {
                            tradeDestination = trade;
                            containers.RemoveAt(0);
                            if (containers.Count <= 0) //Spawn a crate on free pellets until the order is fulfilled.
                                break;
                        }
                    }
                }

                if (tradeDestination != null)
                    break;
            }

            return tradeDestination;
        }

        private void GetTradeStations(StationDataComponent data, ref List<EntityUid> ents)
        {
            foreach (var gridUid in data.Grids)
            {
                if (!_tradeQuery.HasComponent(gridUid))
                    continue;

                ents.Add(gridUid);
            }
        }

        private void OnRemoveOrderMessage(EntityUid uid, CargoOrderConsoleComponent component, CargoConsoleRemoveOrderMessage args)
        {
            var station = _station.GetOwningStation(uid);

            if (component.Mode != CargoOrderConsoleMode.DirectOrder)
                return;

            if (!TryGetOrderDatabase(station, out var orderDatabase))
                return;

            RemoveOrder(station.Value, component.Account, args.OrderId, orderDatabase);
        }

        private void OnAddOrderMessage(EntityUid uid, CargoOrderConsoleComponent component, CargoConsoleAddOrderMessage args)
        {
            if (args.Actor is not { Valid: true } player)
                return;

            if (args.Basket.Count <= 0)
                return;

            var stationUid = _station.GetOwningStation(uid);

            if (!TryGetOrderDatabase(stationUid, out var orderDatabase))
                return;

            if (!TryComp<StationBankAccountComponent>(stationUid, out var bank))
                return;

            var availableProducts = GetAvailableProducts((uid, component));
            foreach (var product in args.Basket)
            {
                if (!_protoMan.TryIndex<CargoProductPrototype>(product.Product, out var _))
                {
                    Log.Error($"Tried to add invalid cargo product {product.Product} as order!");
                    return;
                }
                if (!availableProducts.Contains(product.Product))
                    return;
            }

            var targetAccount = component.Mode == CargoOrderConsoleMode.SendToPrimary ? bank.PrimaryAccount : component.Account;

            var data = GetOrderData(args, GenerateOrderId(orderDatabase), component.Account);

            if (!TryAddOrder(stationUid.Value, targetAccount, data, orderDatabase))
            {
                PlayDenySound(uid, component);
                return;
            }

            // Log order addition
            var adminString = "";
            foreach (var product in args.Basket)
            {
                adminString += $"{product.Quantity} {product},";
            }

            _adminLogger.Add(LogType.Action,
                LogImpact.Low,
                $"{ToPrettyString(player):user} added order [orderId:{data.OrderId}, products:{adminString}, requester:{data.Requester}, reason:{data.Reason}]");

        }

        private void OnOrderUIOpened(EntityUid uid, CargoOrderConsoleComponent component, BoundUIOpenedEvent args)
        {
            var station = _station.GetOwningStation(uid);
            UpdateOrderState(uid, station);
        }

        #endregion

        private void UpdateOrderState(EntityUid consoleUid, EntityUid? station)
        {
            if (!TryComp<CargoOrderConsoleComponent>(consoleUid, out var console))
                return;

            if (!TryComp<StationCargoOrderDatabaseComponent>(station, out var orderDatabase))
                return;

            if (_uiSystem.HasUi(consoleUid, CargoConsoleUiKey.Orders))
            {
                _uiSystem.SetUiState(consoleUid,
                    CargoConsoleUiKey.Orders,
                    new CargoConsoleInterfaceState(
                    MetaData(station.Value).EntityName,
                    GetOutstandingOrderCount((station!.Value, orderDatabase), console.Account),
                    orderDatabase.Capacity,
                    GetNetEntity(station.Value),
                    RelevantOrders((station!.Value, orderDatabase), (consoleUid, console), approved: false),
                    RelevantOrders((station!.Value, orderDatabase), (consoleUid, console), approved: true),
                    GetAvailableProducts((consoleUid, console))
                ));
            }
        }

        /// <summary>
        /// Gets orders relevant to this account, i.e. orders on the account directly or orders on behalf of the account in the primary account.
        /// </summary>
        private List<CargoOrderData> RelevantOrders(Entity<StationCargoOrderDatabaseComponent> station, Entity<CargoOrderConsoleComponent> console, bool approved = false)
        {
            if (!TryComp<StationBankAccountComponent>(station, out var bank))
                return [];

            var ourOrders = station.Comp.Orders[console.Comp.Account];

            IEnumerable<CargoOrderData> orders = ourOrders;

            if (console.Comp.Account != bank.PrimaryAccount)
            {
                var otherOrders = station.Comp.Orders[bank.PrimaryAccount].Where(order => order.Account == console.Comp.Account);
                orders = ourOrders.Concat(otherOrders);
            }

            return [.. orders.Where(order => order.Approved == approved)];
        }
        private void ConsolePopup(EntityUid actor, string text)
        {
            _popup.PopupCursor(text, actor);
        }

        private void PlayDenySound(EntityUid uid, CargoOrderConsoleComponent component)
        {
            if (_timing.CurTime >= component.NextDenySoundTime)
            {
                component.NextDenySoundTime = _timing.CurTime + component.DenySoundDelay;
                _audio.PlayPvs(_audio.ResolveSound(component.ErrorSound), uid);
            }
        }

        private static CargoOrderData GetOrderData(CargoConsoleAddOrderMessage args, int id, ProtoId<CargoAccountPrototype> account)
        {
            return new CargoOrderData(id, args.Basket, args.Requester, args.Reason, account);
        }

        public int GetOutstandingOrderCount(Entity<StationCargoOrderDatabaseComponent> station, ProtoId<CargoAccountPrototype> account)
        {
            var amount = 0;

            if (!TryComp<StationBankAccountComponent>(station, out var bank))
                return amount;

            foreach (var order in station.Comp.Orders[account])
            {
                if (!order.Approved)
                    continue;
                var containers = SortOrders(order);
                amount += containers.Count;
            }

            if (account == bank.PrimaryAccount)
                return amount;

            foreach (var order in station.Comp.Orders[bank.PrimaryAccount])
            {
                if (order.Account != account)
                    continue;
                if (!order.Approved)
                    continue;
                var containers = SortOrders(order);
                amount += containers.Count;
            }

            return amount;
        }

        /// <summary>
        /// Updates all of the cargo-related consoles for a particular station.
        /// This should be called whenever orders change.
        /// </summary>
        private void UpdateOrders(EntityUid dbUid)
        {
            // Order added so all consoles need updating.
            var orderQuery = AllEntityQuery<CargoOrderConsoleComponent>();

            while (orderQuery.MoveNext(out var uid, out var _))
            {
                var station = _station.GetOwningStation(uid);
                if (station != dbUid)
                    continue;

                UpdateOrderState(uid, station);
            }
        }

        public bool AddAndApproveOrder(
            EntityUid dbUid,
            List<CargoOrderItemData> basket,
            string sender,
            string description,
            string dest,
            StationCargoOrderDatabaseComponent component,
            ProtoId<CargoAccountPrototype> account,
            Entity<StationDataComponent> stationData
        )
        {
            // Make an order
            var id = GenerateOrderId(component);
            var order = new CargoOrderData(id, basket, sender, description, account);

            // Approve it now
            order.SetApproverData(dest, sender);
            order.Approved = true;

            // Log order addition
            var adminString = "";
            foreach (var product in order.Basket)
            {
                adminString += $"{product.Quantity} {product.Product},";
            }

            _adminLogger.Add(LogType.Action,
                LogImpact.Low,
                $"AddAndApproveOrder {description} added order [orderId:{order.OrderId}, products:{adminString}, requester:{order.Requester}, reason:{order.Reason}]");

            // Add it to the list
            return TryAddOrder(dbUid, account, order, component) && TryFulfillOrder(stationData, account, order, component).HasValue;
        }

        private bool TryAddOrder(EntityUid dbUid, ProtoId<CargoAccountPrototype> account, CargoOrderData data, StationCargoOrderDatabaseComponent component)
        {
            component.Orders[account].Add(data);
            UpdateOrders(dbUid);
            return true;
        }

        private static int GenerateOrderId(StationCargoOrderDatabaseComponent orderDB)
        {
            // We need an arbitrary unique ID to identify orders, since they may
            // want to be cancelled later.
            return ++orderDB.NumOrdersCreated;
        }

        public void RemoveOrder(EntityUid dbUid, ProtoId<CargoAccountPrototype> account, int index, StationCargoOrderDatabaseComponent orderDB)
        {
            var sequenceIdx = orderDB.Orders[account].FindIndex(order => order.OrderId == index);
            if (sequenceIdx != -1)
            {
                orderDB.Orders[account].RemoveAt(sequenceIdx);
            }
            UpdateOrders(dbUid);
        }

        public void ClearOrders(StationCargoOrderDatabaseComponent component)
        {
            if (component.Orders.Count == 0)
                return;

            component.Orders.Clear();
        }

        private bool PopFrontOrder(StationCargoOrderDatabaseComponent orderDB, ProtoId<CargoAccountPrototype> account, [NotNullWhen(true)] out CargoOrderContainerData? containerOut)
        {
            var orderIdx = orderDB.Orders[account].FindIndex(order => order.Approved);
            if (orderIdx == -1)
            {
                containerOut = null;
                return false;
            }

            var order = orderDB.Orders[account][orderIdx];
            var containers = SortOrders(order);

            if (containers.Count <= 1)
            {
                // Order is complete. Remove from the queue.
                orderDB.Orders[account].RemoveAt(orderIdx);
            }
            if (containers.Count == 0)
            {
                containerOut = null;
                return false;
            }
            containerOut = containers[0];
            return true;
        }

        /// <summary>
        /// Tries to fulfill the next outstanding order.
        /// </summary>
        [PublicAPI]
        private bool FulfillNextOrder(StationCargoOrderDatabaseComponent orderDB, ProtoId<CargoAccountPrototype> account, EntityCoordinates spawn, string? paperProto)
        {
            if (!PopFrontOrder(orderDB, account, out var containerOut))
                return false;
            return FulfillOrder(containerOut, spawn, paperProto);
        }

        /// <summary>
        /// Fulfills the specified cargo order and spawns paper attached to it.
        /// </summary>
        private bool FulfillOrder(CargoOrderContainerData container, EntityCoordinates spawn, string? paperProto)
        {

            EntityUid containerEntity;
            CargoProductPrototype? singleProto = null;

            if (container.IsSingleProduct)
            {
                if (!_protoMan.TryIndex<CargoProductPrototype>(container.Products[0].Product, out singleProto))
                    return false;
                containerEntity = Spawn(singleProto.Product, spawn);
                container.Products[0].HasBeenOrdered = true;
            }
            else
            {
                containerEntity = Spawn(container.Container, spawn);
            }

            _transformSystem.Unanchor(containerEntity, Transform(containerEntity));

            if (!container.IsSingleProduct)
            {
                foreach (var item in container.Products)
                {
                    if (!_protoMan.TryIndex<CargoProductPrototype>(item.Product, out var productProto))
                        return false;
                    var itemEntity = Spawn(productProto.Product, spawn);
                    if (!_container.TryGetContainer(containerEntity, container.ContainerID, out var container1) ||
                        !_container.Insert(itemEntity, container1, force: true))
                    {
                        DebugTools.Assert(
                            $"Failed to insert cargo product into its specified container. This indicates an error in the cargo product definition's YAML as the product should be insertable into its container. {productProto.Name}: {(EntProtoId)container.Container}");
                        QueueDel(itemEntity);
                    }
                    else
                    {
                        item.HasBeenOrdered = true;
                    }
                }
            }

            var printed = Spawn(paperProto, spawn);
            if (TryComp<PaperComponent>(printed, out var paper))
            {
                _metaSystem.SetEntityName(printed, container.LableName);

                _paperSystem.SetContent((printed, paper), container.LableMessage);

                if (TryComp<PaperLabelComponent>(containerEntity, out var label))
                    _slots.TryInsert(containerEntity, label.LabelSlot, printed, null);
            }
            return true;
        }

        private List<CargoOrderContainerData> SortOrders(CargoOrderData order)
        {
            List<CargoOrderContainerData> containers = new();
            foreach (var item in order.Basket)
            {
                if (item == null)
                    continue;
                if (!_protoMan.TryIndex<CargoProductPrototype>(item.Product, out var productProto))
                    continue;
                if (!item.ToBeOrdered)
                    continue;
                if (!item.WithContainer || productProto.Container == null)
                {
                    for (int j = 0; j < item.Quantity; j++)
                    {
                        containers.Add(new CargoOrderContainerData("", "", item));
                    }
                    continue;
                }
                var foundMatch = false;
                for (int i = 0; i < containers.Count; i++)
                {
                    if (!_protoMan.TryIndex<CargoCratePrototype>(productProto.Container, out var crate))
                        continue;
                    if (containers[i].Container != ""
                        && (EntProtoId)containers[i].Container == crate.Entity
                        && GetContainerEntityCount(containers[i]) <= containers[i].MaxItems - item.Quantity
                        && containers[i].CrateRequired == crate.Required)
                    {
                        for (int j = 0; j < item.Quantity; j++)
                        {
                            containers[i].Products.Add(item);
                        }
                        foundMatch = true;
                        break;
                    }
                }
                if (!foundMatch)
                {
                    if (!_protoMan.TryIndex<CargoCratePrototype>(productProto.Container, out var crate))
                        continue;
                    containers.Add(new CargoOrderContainerData(crate.Entity, crate.ContainerId, crateRequired: crate.Required, maxItems: crate.MaxItems));
                    for (int j = 0; j < item.Quantity; j++)
                    {
                        containers.Last().Products.Add(item);
                    }
                }
            }
            foreach (var container in containers)
            {
                container.LableMessage = GetContainerLabel(container, order);
                container.LableName = Loc.GetString("cargo-console-paper-print-name", ("orderNumber", order.OrderId));
                var parcel = (ProtoId<CargoCratePrototype>)"WrappedParcel";
                if (!container.IsSingleProduct && GetContainerEntityCount(container) == 1 && !container.CrateRequired)
                {
                    if (!_protoMan.Resolve<CargoCratePrototype>(parcel, out var crate))
                        continue;
                    container.Container = crate.Entity;
                    container.ContainerID = crate.ContainerId;
                }
            }
            return containers;
        }

        private int GetContainerEntityCount(CargoOrderContainerData container)
        {
            return container.Products.Count;
            //
            //var count = 0;
            //foreach (var item in container.Products)
            //{
            //    if (!_protoMan.TryIndex<CargoProductPrototype>(item.Product, out var productProto))
            //        return 0;
            //    count += item.Quantity * productProto.Products.Count;
            //}
            //return count;
            //
        }

        private string GetContainerLabel(CargoOrderContainerData container, CargoOrderData order)
        {
            var accountProto = _protoMan.Index(order.Account);
            string message;
            if (container.IsSingleProduct)
            {
                if (!_protoMan.TryIndex<CargoProductPrototype>(container.Products[0].Product, out var singleProto))
                    return "";
                message = Loc.GetString(
                    "cargo-console-paper-print-text",
                    ("orderNumber", order.OrderId),
                    ("itemName", Loc.GetString(singleProto!.Name)),
                    ("requester", order.Requester),
                    ("reason", string.IsNullOrWhiteSpace(order.Reason) ? Loc.GetString("cargo-console-paper-reason-default") : order.Reason),
                    ("account", Loc.GetString(accountProto.Name)),
                    ("accountcode", Loc.GetString(accountProto.Code)),
                    ("approver", string.IsNullOrWhiteSpace(order.Approver) ? Loc.GetString("cargo-console-paper-approver-default") : order.Approver));
            }
            else
            {
                message = Loc.GetString("cargo-console-paper-print-header", ("orderNumber", order.OrderId));
                message += "\n";
                var groupedProducts = from x in container.Products
                                      group x by x.Product into g
                                      let count = g.Count()
                                      orderby count descending
                                      select new { Value = g.Key, Count = count };

                foreach (var product in groupedProducts)
                {
                    if (!_protoMan.TryIndex<CargoProductPrototype>(product.Value, out var productProto))
                    {
                        message += "\n";
                        continue;
                    }
                    message += Loc.GetString("cargo-console-paper-print-item",
                        ("itemName", Loc.GetString(productProto.Name)),
                        ("orderQuantity", product.Count));
                    message += "\n";
                }
                message += Loc.GetString("cargo-console-paper-print-footer",
                    ("requester", order.Requester),
                    ("reason", string.IsNullOrWhiteSpace(order.Reason) ? Loc.GetString("cargo-console-paper-reason-default") : order.Reason),
                    ("account", Loc.GetString(accountProto.Name)),
                    ("accountcode", Loc.GetString(accountProto.Code)),
                    ("approver", string.IsNullOrWhiteSpace(order.Approver) ? Loc.GetString("cargo-console-paper-approver-default") : order.Approver));
            }
            return message;
        }

        public int GetOrderCost(CargoOrderData order)
        {
            var cost = 0;
            foreach (var product in order.Basket)
            {
                if (!_protoMan.TryIndex<CargoProductPrototype>(product.Product, out var productProto))
                {
                    return 0;
                }
                cost += productProto.Cost * product.Quantity;
            }
            return cost;
        }

        public List<ProtoId<CargoProductPrototype>> GetAvailableProducts(Entity<CargoOrderConsoleComponent> ent)
        {
            if (_station.GetOwningStation(ent) is not { } station ||
                !TryComp<StationCargoOrderDatabaseComponent>(station, out var db))
            {
                return new List<ProtoId<CargoProductPrototype>>();
            }

            var products = new List<ProtoId<CargoProductPrototype>>();

            // Note that a market must be both on the station and on the console to be available.
            var markets = ent.Comp.AllowedGroups.Intersect(db.Markets).ToList();
            foreach (var product in _protoMan.EnumeratePrototypes<CargoProductPrototype>())
            {
                if (!markets.Contains(product.Group))
                    continue;

                products.Add(product.ID);
            }

            return products;
        }

        #region Station

        private bool TryGetOrderDatabase([NotNullWhen(true)] EntityUid? stationUid, [MaybeNullWhen(false)] out StationCargoOrderDatabaseComponent dbComp)
        {
            return TryComp(stationUid, out dbComp);
        }

        #endregion
    }
}
