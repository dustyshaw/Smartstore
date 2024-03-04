﻿using Microsoft.AspNetCore.Http;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Common;
using Smartstore.Core.Data;
using Smartstore.Engine.Modularity;

namespace Smartstore.Core.Checkout.Orders.Requirements
{
    public class PaymentMethodRequirement : CheckoutRequirementBase
    {
        private bool? _skip;
        private readonly SmartDbContext _db;
        private readonly IPaymentService _paymentService;
        private readonly IOrderCalculationService _orderCalculationService;
        private readonly ICheckoutStateAccessor _checkoutStateAccessor;
        private readonly PaymentSettings _paymentSettings;
        private readonly ShoppingCartSettings _shoppingCartSettings;

        public PaymentMethodRequirement(
            SmartDbContext db,
            IPaymentService paymentService,
            IOrderCalculationService orderCalculationService,
            ICheckoutStateAccessor checkoutStateAccessor,
            IHttpContextAccessor httpContextAccessor,
            PaymentSettings paymentSettings,
            ShoppingCartSettings shoppingCartSettings)
            : base(httpContextAccessor)
        {
            _db = db;
            _paymentService = paymentService;
            _orderCalculationService = orderCalculationService;
            _checkoutStateAccessor = checkoutStateAccessor;
            _paymentSettings = paymentSettings;
            _shoppingCartSettings = shoppingCartSettings;
        }

        protected override string ActionName => "PaymentMethod";

        public override int Order => 40;

        public override async Task<CheckoutRequirementResult> CheckAsync(ShoppingCart cart, object model = null)
        {
            var state = _checkoutStateAccessor.CheckoutState;
            var attributes = cart.Customer.GenericAttributes;
            List<Provider<IPaymentMethod>> providers = null;

            if (model != null 
                && model is string paymentMethod 
                && IsSameRoute(HttpMethods.Post, ActionName))
            {
                var provider = await _paymentService.LoadPaymentProviderBySystemNameAsync(paymentMethod, true, cart.StoreId);
                if (provider == null)
                {
                    return new(false);
                }

                attributes.SelectedPaymentMethod = paymentMethod;
                await attributes.SaveChangesAsync();

                var form = HttpContext.Request.Form;
                if (form != null)
                {
                    // Save payment data so that the user must not re-enter it.
                    foreach (var pair in form)
                    {
                        var v = pair.Value;
                        state.PaymentData[pair.Key] = v.Count == 2 && v[0] != null && v[0] == "true"
                            ? "true"
                            : v.ToString();
                    }
                }

                // Validate payment data.
                var validationResult = await provider.Value.ValidatePaymentDataAsync(form);
                if (validationResult.IsValid)
                {
                    var paymentInfo = await provider.Value.GetPaymentInfoAsync(form);
                    HttpContext.Session.TrySetObject(CheckoutState.OrderPaymentInfoName, paymentInfo);
                    state.PaymentSummary = await provider.Value.GetPaymentSummaryAsync();

                    return new(true);
                }
                else
                {
                    var errors = validationResult.Errors
                        .Select(x => new CheckoutWorkflowError(x.PropertyName, x.ErrorMessage))
                        .ToArray();

                    return new(false, errors);
                }
            }

            // INFO: cache result. Only needs to be processed once, not every time the requirements are checked.
            if (_skip == null)
            {
                var cartTotal = (Money?)await _orderCalculationService.GetShoppingCartTotalAsync(cart, false);
                state.IsPaymentRequired = cartTotal.GetValueOrDefault() != decimal.Zero;

                if (state.IsPaymentRequired)
                {
                    if (_paymentSettings.SkipPaymentSelectionIfSingleOption)
                    {
                        providers ??= await GetPaymentMethods(cart);

                        state.CustomProperties["HasOnlyOneActivePaymentMethod"] = providers.Count == 1;
                        state.IsPaymentSelectionSkipped = providers.Count == 1 && !providers[0].Value.RequiresPaymentSelection;

                        if (state.IsPaymentSelectionSkipped)
                        {
                            attributes.SelectedPaymentMethod = providers[0].Metadata.SystemName;
                            await attributes.SaveChangesAsync();
                        }
                    }
                }
                else
                {
                    state.IsPaymentSelectionSkipped = true;
                }

                // INFO: "_skip" is only set to "true" if the payment selection is always skipped without any exception.
                _skip = state.IsPaymentSelectionSkipped;
            }

            if (_shoppingCartSettings.QuickCheckoutEnabled 
                && state.IsPaymentRequired
                && attributes.SelectedPaymentMethod.IsEmpty())
            {
                providers ??= await GetPaymentMethods(cart);

                var preferredMethod = attributes.PreferredPaymentMethod;
                if (preferredMethod.HasValue() && providers.Any(x => x.Metadata.SystemName.EqualsNoCase(preferredMethod)))
                {
                    attributes.SelectedPaymentMethod = preferredMethod;
                    await attributes.SaveChangesAsync();

                    state.IsPaymentSelectionSkipped = true;
                }

                if (attributes.SelectedPaymentMethod.IsEmpty())
                {
                    // Fallback to last used payment.
                    var quickCheckoutPayments = providers
                        .Where(x => !x.Value.RequiresPaymentSelection)
                        .Select(x => x.Metadata.SystemName)
                        .ToArray();
                    if (quickCheckoutPayments.Length > 0)
                    {
                        var lastOrder = await _db.Orders
                            .Where(x => quickCheckoutPayments.Contains(x.PaymentMethodSystemName))
                            .ApplyStandardFilter(cart.Customer.Id, cart.StoreId)
                            .FirstOrDefaultAsync();
                        if (lastOrder != null)
                        {
                            var provider = providers.FirstOrDefault(x => x.Metadata.SystemName.EqualsNoCase(lastOrder.PaymentMethodSystemName));
                            if (provider != null)
                            {
                                var paymentRequest = await provider.Value.CreateProcessPaymentRequestAsync(cart, lastOrder);
                                if (paymentRequest != null)
                                {
                                    attributes.SelectedPaymentMethod = provider.Metadata.SystemName;
                                    await attributes.SaveChangesAsync();

                                    HttpContext.Session.TrySetObject(CheckoutState.OrderPaymentInfoName, paymentRequest);
                                    state.PaymentSummary = await provider.Value.GetPaymentSummaryAsync();
                                    state.IsPaymentSelectionSkipped = true;
                                }
                            }
                        }
                    }
                }
            }

            return new(attributes.SelectedPaymentMethod.HasValue(), null, _skip ?? false);
        }

        private async Task<List<Provider<IPaymentMethod>>> GetPaymentMethods(ShoppingCart cart)
        {
            var providers = await _paymentService.LoadActivePaymentProvidersAsync(cart, cart.StoreId);
            
            if (cart.ContainsRecurringItem())
            {
                providers = providers.Where(x => x.Value.RecurringPaymentType > RecurringPaymentType.NotSupported);
            }

            return providers.ToList();
        }
    }
}
