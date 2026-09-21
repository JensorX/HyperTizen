using System;
using Tizen.Applications;
using Tizen.Applications.Notifications;
using Tizen.System;
using System.Threading.Tasks;

namespace HyperTizen
{
    class App : ServiceApplication
    {
        public static HyperionClient client;
        protected override void OnCreate()
        {
            base.OnCreate();

            // STEP 1: Load preferences FIRST (before any testing)
            if (!Preference.Contains("enabled")) Preference.Set("enabled", "true");
            if (!Preference.Contains("diagnosticMode"))
            {
                Preference.Set("diagnosticMode", Globals.DIAGNOSTIC_MODE_ENABLED ? "true" : "false");
            }

            // STEP 2: Initialize Globals with preferences
            Globals.Instance.LoadPreferencesEarly();

            // STEP 3: Start WebSocket servers
            Helper.Log.StartWebSocketServer(45678);

            // Start WebSocket control server on port 45677 for UI control
            Helper.Log.Write(Helper.eLogType.Info, "Launching control WebSocket server task...");
            Task.Run(async () =>
            {
                try
                {
                    await WebSocket.WebSocketServer.StartServerAsync();
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"Control WebSocket server task crashed: {ex.Message}");

                    // Show TV notification so user can see the error even without logs
                    try
                    {
                        Notification crashNotif = new Notification
                        {
                            Title = "WebSocket Critical Error",
                            Content = $"Task crashed: {ex.Message}",
                            Count = 1
                        };
                        NotificationManager.Post(crashNotif);
                    }
                    catch { /* Ignore notification errors */ }
                }
            });

            // STEP 4: Wait for network stack (10 seconds as requested)
            Helper.Log.Write(Helper.eLogType.Info, "Waiting 10 seconds for network stack initialization...");
            System.Threading.Thread.Sleep(10000);
            Helper.Log.Write(Helper.eLogType.Info, "Network stack ready - continuing startup");

            // STEP 5: Run diagnostics (ONLY if not in diagnostic mode)
            if (!Globals.Instance.DiagnosticMode)
            {
                try
                {
                    DiagnosticCapture.RunDiagnostics();
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Error, $"Diagnostic testing failed: {ex.Message}");
                    // Continue startup
                }
            }
            else
            {
                Helper.Log.Write(Helper.eLogType.Info, "DIAGNOSTIC MODE: Skipping DiagnosticCapture tests");
            }

            // STEP 6: Continue normal startup
            Display.StateChanged += Display_StateChanged;
            client = new HyperionClient();

            // Show service started notification (always shown)
            Notification startNotif = new Notification
            {
                Title = "HyperTizen Service",
                Content = "Service started",
                Count = 1
            };
            NotificationManager.Post(startNotif);
        }

        private void Display_StateChanged(object sender, DisplayStateChangedEventArgs e)
        {
            if (e.State == DisplayState.Off)
            {
                Task.Run(() => client.Stop());
            } else if (e.State == DisplayState.Normal)
            {
                Task.Run(() => client.Start());
            }
        }

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);

            // The visible Web application explicitly starts this service. Reply to
            // the launch request so it can begin polling /health immediately.
            try
            {
                ReceivedAppControl received = e.ReceivedAppControl;
                if (received != null && received.IsReplyRequest)
                {
                    received.ReplyToLaunchRequest(new AppControl(), AppControlReplyResult.Succeeded);
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"Could not reply to UI launch request: {ex.Message}");
            }
        }

        protected override void OnDeviceOrientationChanged(DeviceOrientationEventArgs e)
        {
            base.OnDeviceOrientationChanged(e);
        }

        protected override void OnLocaleChanged(LocaleChangedEventArgs e)
        {
            base.OnLocaleChanged(e);
        }

        protected override void OnLowBattery(LowBatteryEventArgs e)
        {
            base.OnLowBattery(e);
        }

        protected override void OnLowMemory(LowMemoryEventArgs e)
        {
            base.OnLowMemory(e);
        }

        protected override void OnRegionFormatChanged(RegionFormatChangedEventArgs e)
        {
            base.OnRegionFormatChanged(e);
        }

        protected override void OnTerminate()
        {
            // Show service stopped notification (always shown)
            Notification stopNotif = new Notification
            {
                Title = "HyperTizen Service",
                Content = "Service stopped",
                Count = 1
            };
            NotificationManager.Post(stopNotif);

            // Stop WebSocket server
            Helper.Log.StopWebSocketServer();
            base.OnTerminate();
        }

        static void Main(string[] args)
        {
            App app = new App();
            app.Run(args);
        }
        public static class Configuration
        {
            public static string RPCServer
            {
                get { return Preference.Contains("rpcServer") ? Preference.Get<string>("rpcServer") : null; }
                set { Preference.Set("rpcServer", value ?? string.Empty); }
            }

            public static bool Enabled
            {
                get
                {
                    string value = Preference.Contains("enabled") ? Preference.Get<string>("enabled") : "true";
                    return bool.TryParse(value, out bool enabled) && enabled;
                }
                set { Preference.Set("enabled", value ? "true" : "false"); }
            }
        }
    }
}
