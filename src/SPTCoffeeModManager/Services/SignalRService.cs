using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace SPTCoffeeModManager.Services
{
    /// <summary>
    /// A lightweight singleton service that manages a SignalR Hub connection and exposes events for notifications.
    /// Subscribers (typically UI) should marshal to the UI thread (Dispatcher) when updating controls.
    /// </summary>
    public sealed class SignalRService
    {
        private static readonly Lazy<SignalRService> _lazy = new(() => new SignalRService());
        public static SignalRService Instance => _lazy.Value;

        private HubConnection? _hubConnection;

        private SignalRService() { }

        public bool IsConnected => _hubConnection != null && _hubConnection.State == HubConnectionState.Connected;

        // Notification events - string message payload from server
        public event Action<string>? ServerRestarting;
        public event Action<string>? SptServerOffline;
        public event Action<string>? SptServerOnline;
        public event Action<string>? SptServerRestarting;
        public event Action<string>? SptServerUpdating;
        public event Action<string>? HeadlessOffline;
        public event Action<string>? HeadlessOnline;
        public event Action<string>? HeadlessRestarted;

        public event Action<string>? Connected; // optional
        public event Action<Exception>? ConnectionFailed;

        public async Task StartAsync(string baseUrl)
        {
            // If already started and url is same, ignore
            if (_hubConnection != null)
            {
                if (_hubConnection.State == HubConnectionState.Connected)
                    return;
            }

            _hubConnection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl}/api/hub")
                .WithAutomaticReconnect()
                .Build();

            // Register handlers
            _hubConnection.On<string>("ServerRestarting", (message) => { ServerRestarting?.Invoke(message); });
            _hubConnection.On<string>("SptServerOffline", (message) => { SptServerOffline?.Invoke(message); });
            _hubConnection.On<string>("SptServerOnline", (message) => { SptServerOnline?.Invoke(message); });
            _hubConnection.On<string>("SptServerRestarting", (message) => { SptServerRestarting?.Invoke(message); });
            _hubConnection.On<string>("SptServerUpdating", (message) => { SptServerUpdating?.Invoke(message); });
            _hubConnection.On<string>("HeadlessOffline", (message) => { HeadlessOffline?.Invoke(message); });
            _hubConnection.On<string>("HeadlessOnline", (message) => { HeadlessOnline?.Invoke(message); });
            _hubConnection.On<string>("HeadlessRestarted", (message) => { HeadlessRestarted?.Invoke(message); });

            try
            {
                await _hubConnection.StartAsync();
                Connected?.Invoke("Connected");
            }
            catch (Exception ex)
            {
                ConnectionFailed?.Invoke(ex);
            }
        }

        public async Task StopAsync()
        {
            if (_hubConnection != null)
            {
                try
                {
                    await _hubConnection.StopAsync();
                }
                catch { }
                finally
                {
                    await _hubConnection.DisposeAsync();
                    _hubConnection = null;
                }
            }
        }
    }
}

