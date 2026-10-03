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
        private readonly object _displayTransitionLock = new object();
        private Task _pendingDisplayTransition = Task.CompletedTask;
        private bool _displayWasOff;
        private DisplayState _requestedDisplayState = DisplayState.Normal;
        protected override void OnCreate()
        {
            base.OnCreate();

            // STEP 1: Load preferences FIRST (before any testing)
            if (!Preference.Contains("enabled")) Preference.Set("enabled", "false");

            // CRITICAL: Force diagnostic mode based on build constant
            // This OVERRIDES any saved preference to ensure build const is respected
            // Set Globals.DIAGNOSTIC_MODE_ENABLED = true in code to enable diagnostic mode
            Preference.Set("diagnosticMode", Globals.DIAGNOSTIC_MODE_ENABLED ? "true" : "false");

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
            client = new HyperionClient();
            Display.StateChanged += Display_StateChanged;
            QueueDisplayTransition(DisplayState.Normal);

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
            QueueDisplayTransition(e.State);
        }

        private void QueueDisplayTransition(DisplayState state)
        {
            if (state != DisplayState.Off && state != DisplayState.Normal) return;

            // Queue in event order: Task.Run plus a semaphore can reverse Off/Normal.
            lock (_displayTransitionLock)
            {
                _requestedDisplayState = state;
                _pendingDisplayTransition = _pendingDisplayTransition.ContinueWith(async _ =>
                {
                    try
                    {
                        if (state == DisplayState.Off)
                        {
                            _displayWasOff = true;
                            Helper.Log.Write(Helper.eLogType.Info, "Display off: stopping capture");
                            await client.Stop();
                        }
                        else
                        {
                            // HDMI and network services may not be ready immediately on wake.
                            if (_displayWasOff) await Task.Delay(2000);
                            lock (_displayTransitionLock)
                            {
                                if (_requestedDisplayState != DisplayState.Normal) return;
                            }
                            if (_displayWasOff) await client.Stop();
                            if (client.IsRunning) return;
                            lock (_displayTransitionLock)
                            {
                                if (_requestedDisplayState != DisplayState.Normal) return;
                            }
                            _displayWasOff = false;
                            Helper.Log.Write(Helper.eLogType.Info, "Display normal: starting capture");
                            _ = Task.Run(async () =>
                            {
                                lock (_displayTransitionLock)
                                {
                                    if (_requestedDisplayState != DisplayState.Normal) return;
                                }
                                await client.Start();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Helper.Log.Write(Helper.eLogType.Error, $"Display transition failed: {ex.Message}");
                    }
                }).Unwrap();
            }
        }

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);
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
            Display.StateChanged -= Display_StateChanged;
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
            public static string RPCServer = Preference.Contains("rpcServer") ? Preference.Get<string>("rpcServer") : null;
            public static bool Enabled = bool.Parse(Preference.Get<string>("enabled"));
        }
    }
}
