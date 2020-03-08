﻿using Courier.Service.Interfaces;
using Courier.Service.Models;
using Courier.Service.Models.Notification;
using Courier.Service.Models.ParcelLabel;
using Courier.Service.Models.ParcelPickup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Courier.Service.Services
{
    public class CourierHandlerService : IHostedService, IObserver<CourierRequest>
    {
        private IDisposable unsubscriber;
        private readonly ILogger<CourierHandlerService> logger;
        private readonly IEventBusService<CourierRequest> courierService;
        private readonly ICourierDetailsService courierDetailsService;
        private readonly IParcelPickupService parcelService;
        private readonly IParcelLabelService labelService;
        private readonly IACEService aceService;
        private readonly INotificationService notificationService;

        public CourierHandlerService(
            ILogger<CourierHandlerService> logger,
            IEventBusService<CourierRequest> courierService,
            ICourierDetailsService courierDetailsService,
            IParcelPickupService parcelService,
            IParcelLabelService labelService,
            IACEService aceService,
            INotificationService notificationService)
        {
            this.logger = logger;
            this.courierService = courierService;
            this.courierDetailsService = courierDetailsService;
            this.parcelService = parcelService;
            this.labelService = labelService;
            this.aceService = aceService;
            this.notificationService = notificationService;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            unsubscriber = courierService.Subscribe(this);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            unsubscriber.Dispose();
            return Task.CompletedTask;
        }

        public virtual void OnNext(CourierRequest request)
        {
            _ = Process(request);
        }

        public virtual void OnCompleted()
        {
        }

        public virtual void OnError(Exception error)
        {
        }

        public async Task Process(CourierRequest request)
        {
            var consignment = new Consignment();
            var courierDetails = new CourierDetails();

            try
            {
                // Get Courier Details 
                courierDetails = await courierDetailsService.Get(request.BranchId, request.DeliveryType);

                // Parcel Pickup
                var parcelPickupRequest = new ParcelPickupRequest
                {
                    Carrier = request.Carrier,
                    Caller = request.Caller,
                    Service_Code = courierDetails.ServiceCode,
                    Pickup_Date_Time = request.Parcel_Pickup_Date_Time,
                    Parcel_Quantity = request.Parcel_Quantity,
                    Pickup_Address = new PickupAddress { Phone = request.Parcel_Pickup_Address.Phone, Site_Code = courierDetails.SiteCode },
                    Delivery_Address = request.Parcel_Delivery_Address
                };

                // Create a jobNumber
                var jobNumber = await parcelService.ParcelPickup(parcelPickupRequest, courierDetails);

                var parcelLabelRequest = new ParcelLabelRequest
                {
                    Carrier = request.Carrier.ToUpper(),
                    Logo_Id = courierDetails.LogoId,
                    Job_Number = Convert.ToInt32(jobNumber),
                    Sender_Details = request.Label_Sender_Details,
                    Receiver_Details = request.Label_Receiver_Details,
                    Pickup_Address = new PickAddress { Site_Code = Convert.ToInt32(courierDetails.SiteCode) },
                    Delivery_Address = request.Label_Delivery_Address
                };

                // Create a label
                consignment = await labelService.Create(parcelLabelRequest, courierDetails);

                // Get the label ConsignmentURL
                consignment.ConsignmentURL = await labelService.GetStatus(consignment.ConsignmentId, courierDetails);

                // Update ACE with the ConsignmentUrl (NPLU)
                // await aceService.UpdateParcelLabel(request.BranchId.ToString(), request.FullOrderNumber, consignment, courierDetails.Username);

                // Setup Label is Ready notification
                var notificationRequest = new NotificationRequest
                {
                    TransactionId = DateTime.Now.Ticks.ToString(),
                    OrderNo = request.FullOrderNumber,
                    BranchId = request.BranchId,
                    BranchName = request.Caller,
                    CustomerId = request.Label_Receiver_Details.Name,
                    CustomerEmail = "solly.fathi@placemakers.co.nz"
                };

                // Send the notification
                await notificationService.Send(notificationRequest, courierDetails.Username, consignment.ConsignmentId);
            }

            catch (ServiceException ex)
            {
                // Update ACE of any consignment related errors
                consignment.Details = ex.Message;
                await aceService.UpdateParcelLabel(request.BranchId.ToString(), request.FullOrderNumber, consignment, courierDetails.Username);
            }

            catch (Exception ex)
            {
                // Log any other errors thrown by the service
                logger.LogError($"Something went wrong: {ex.Message}");
            }
        }
    }
}