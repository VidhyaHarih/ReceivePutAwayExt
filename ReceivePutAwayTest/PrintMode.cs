using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

using PX.Common;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.BarcodeProcessing;

using PX.Objects.Common;
using PX.Objects.AP;
using PX.Objects.IN;
using PX.Objects.IN.WMS;
using PX.Objects.IN.Overrides.INDocumentRelease;
using PX.Objects.SO.WMS;
using System.Runtime.Remoting.Messaging;

namespace PX.Objects.PO.WMS
{
    using static PX.BarcodeProcessing.BarcodeDrivenStateMachine<ReceivePutAway, ReceivePutAway.Host>;
    using static PX.Objects.PO.WMS.ReceivePutAway;
    using WMSBase = WarehouseManagementSystem<ReceivePutAway, ReceivePutAway.Host>;

    public class ReceivePutAwayExt : ReceivePutAway.ScanExtension
    {
        public static bool IsActive() => true;

        public sealed class PrintMode : ReceivePutAway.ScanMode
        {
            public const string Value = "PRTLBL";
            public class value : BqlString.Constant<value> { public value() : base(PrintMode.Value) { } }

            //public PrintMode.Logic Body => Get<PrintMode.Logic>();

            public override string Code => Value;
            public override string Description => Msg.Description;

            #region State Machine
            protected override IEnumerable<ScanState<ReceivePutAway>> CreateStates()
            {
                yield return new ReceivePutAway.ReceiveMode.ReceiptState();
                yield return new WMSBase.InventoryItemState() { AlternateType = INPrimaryAlternateType.CPN };
            }

            protected override IEnumerable<ScanTransition<ReceivePutAway>> CreateTransitions()
            {
                return StateFlow(flow => flow
                            .From<ReceivePutAway.ReceiveMode.ReceiptState>()
                            .NextTo<ReceivePutAway.InventoryItemState>());
            }

            protected override IEnumerable<ScanCommand<ReceivePutAway>> CreateCommands()
            {
                yield return new PrintMode.PrintCommand();
            }


            protected override IEnumerable<ScanRedirect<ReceivePutAway>> CreateRedirects() => AllWMSRedirects.CreateFor<ReceivePutAway>();

            protected override void ResetMode(bool fullReset)
            {
                base.ResetMode(fullReset);

                if (fullReset)
                {
                    Basis.PONbr = null;
                    Basis.PrevInventoryID = null;
                }
            }
            #endregion            

            #region Commands            
            public sealed class PrintCommand : ScanCommand
            {
                public override string Code => "PRINT";
                public override string ButtonName => "printLabels";
                public override string DisplayName => Msg.DisplayName;
                protected override bool IsEnabled => true;

                protected override bool Process()
                {
                    Get<Logic>().PrintLabels(false);
                    return true;
                }

                #region Logic
                public class Logic : ScanExtension
                {
                    public virtual void PrintLabels(bool printByDeviceHub)
                    {
                        if (printByDeviceHub) // print using DeviceHub
                        {
                            var userSetup = UserSetup.For(Basis);
                            bool printLabels = userSetup.PrintInventoryLabelsAutomatically == true;
                            string printLabelsReportID = printLabels ? userSetup.InventoryLabelsReportID : null;
                            bool printReceipt = userSetup.PrintPurchaseReceiptAutomatically == true;

                            var receipt = Basis.Receipt;

                            var receiptGraph = PXGraph.CreateInstance<POReceiptEntry>();
                            receiptGraph.Document.Current = receiptGraph.Document.Search<POReceipt.receiptNbr>(receipt.ReceiptNbr, receipt.ReceiptType);
                            receiptGraph.releaseFromHold.Press();
                            receipt = receiptGraph.Document.Current;

                            if (PXAccess.FeatureInstalled<CS.FeaturesSet.deviceHub>())
                            {
                                var inGraph = Lazy.By(() => PXGraph.CreateInstance<INReceiptEntry>());

                                if (!string.IsNullOrEmpty(printLabelsReportID))
                                {
                                    string inventoryRefNbr = POReceipt.PK.Find(inGraph.Value, receipt)?.InvtRefNbr;
                                    if (inventoryRefNbr != null)
                                    {
                                        var reportParameters = new Dictionary<string, string>()
                                        {
                                            [nameof(INRegister.RefNbr)] = inventoryRefNbr
                                        };

                                        DeviceHubTools.PrintReportViaDeviceHub<CR.BAccount>(inGraph.Value, printLabelsReportID, reportParameters, INNotificationSource.None, null);
                                    }
                                }

                                if (printReceipt)
                                {
                                    var reportParameters = new Dictionary<string, string>()
                                    {
                                        [nameof(POReceipt.ReceiptType)] = receipt.ReceiptType,
                                        [nameof(POReceipt.ReceiptNbr)] = receipt.ReceiptNbr
                                    };

                                    Vendor vendor = Vendor.PK.Find(inGraph.Value, receipt.VendorID);
                                    DeviceHubTools.PrintReportViaDeviceHub(inGraph.Value, "PO646000", reportParameters, PONotificationSource.Vendor, vendor);
                                }
                            }
                        }
                        else
                        {
                            // add your logic to print labels
                        }
                    }
                }
                #endregion

                #region Messages
                [PXLocalizable]
                public abstract class Msg
                {
                    public const string DisplayName = "Print Labels";
                }
                #endregion
            }
            #endregion

            #region Redirect
            public sealed class RedirectFrom<TForeignBasis> : WMSBase.RedirectFrom<TForeignBasis>.SetMode<PrintMode>
              where TForeignBasis : PXGraphExtension, IBarcodeDrivenStateMachine
            {
                public override string Code => "PRTLBL";
                public override string DisplayName => Msg.DisplayName;

                private string RefNbr { get; set; }

                public override bool IsPossible
                {
                    get
                    {
                        return true;
                    }
                }

                protected override bool PrepareRedirect()
                {
                    if (Basis is ReceivePutAway rpa && rpa.RefNbr != null)
                    {
                        if (rpa.FindMode<ReceivePutAwayExt.PrintMode>().TryValidate(rpa.Receipt).By<ReceivePutAway.ReceiptState>() is Validation valid && valid.IsError == true)
                        {
                            rpa.ReportError(valid.Message, valid.MessageArgs);
                            return false;
                        }
                        else
                            RefNbr = rpa.RefNbr;
                    }

                    return true;
                }

                protected override void CompleteRedirect()
                {
                    if (Basis is ReceivePutAway rpa && rpa.CurrentMode.Code != ReceivePutAway.ReturnMode.Value && this.RefNbr != null)
                        if (rpa.TryProcessBy(ReceivePutAway.ReceiptState.Value, RefNbr, StateSubstitutionRule.KeepAll & ~StateSubstitutionRule.KeepPositiveReports))
                        {
                            rpa.SetDefaultState();
                            RefNbr = null;
                        }
                }

                #region Messages
                [PXLocalizable]
                public abstract class Msg
                {
                    public const string DisplayName = "Print Label";
                }
                #endregion
            }
            #endregion

            #region Messages
            [PXLocalizable]
            public abstract class Msg
            {
                public const string Description = "Print Labels";
            }
            #endregion
        }

        [PXOverride]
        public virtual IEnumerable<ScanMode<ReceivePutAway>> CreateScanModes(Func<IEnumerable<ScanMode<ReceivePutAway>>> base_CreateScanModes)
        {
            foreach (var mode in base_CreateScanModes())
                yield return mode;

            yield return new PrintMode();
        }


        [PXOverride]
        public virtual ScanMode<ReceivePutAway> DecorateScanMode(ScanMode<ReceivePutAway> original, Func<ScanMode<ReceivePutAway>, ScanMode<ReceivePutAway>> base_DecorateScanMode)
        {
            var mode = base_DecorateScanMode(original);
            if (mode is ReceivePutAway.ReceiveMode receiveMode)
            {
                receiveMode
                    .Intercept.CreateRedirects.ByAppend(() => new ScanRedirect<ReceivePutAway>[] { new PrintMode.RedirectFrom<ReceivePutAway>() });
            }
            if (mode is ReceivePutAway.PutAwayMode putawayMode)
            {
                putawayMode
                    .Intercept.CreateRedirects.ByAppend(() => new ScanRedirect<ReceivePutAway>[] { new PrintMode.RedirectFrom<ReceivePutAway>() });
            }
            return mode;
        }        
    }
}