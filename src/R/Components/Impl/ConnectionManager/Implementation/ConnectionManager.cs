// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Disposables;
using Microsoft.Common.Core.Logging;
using Microsoft.Common.Core.Shell;
using Microsoft.R.Components.ConnectionManager.Implementation.View;
using Microsoft.R.Components.ConnectionManager.Implementation.ViewModel;
using Microsoft.R.Components.Extensions;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.Settings;
using Microsoft.R.Components.StatusBar;
using Microsoft.R.Host.Client;
using Microsoft.R.Interpreters;

namespace Microsoft.R.Components.ConnectionManager.Implementation {
    internal class ConnectionManager : IConnectionManager {
        private readonly IRSettings _settings;
        private readonly ICoreShell _shell;
        private readonly IStatusBar _statusBar;
        private readonly IRSessionProvider _sessionProvider;
        private readonly DisposableBag _disposableBag;
        private readonly ConnectionStatusBarViewModel _statusBarViewModel;
        private readonly ConcurrentDictionary<Uri, IConnection> _userConnections;

        public bool IsConnected { get; private set; }
        public IConnection ActiveConnection { get; private set; }
        public ReadOnlyCollection<IConnection> RecentConnections { get; private set; }
        public IConnectionManagerVisualComponent VisualComponent { get; private set; }

        public event EventHandler RecentConnectionsChanged;
        public event EventHandler<ConnectionEventArgs> ConnectionStateChanged;

        public ConnectionManager(IStatusBar statusBar, IRSettings settings, IRInteractiveWorkflow interactiveWorkflow) {
            _statusBar = statusBar;
            _sessionProvider = interactiveWorkflow.RSessions;
            _settings = settings;
            _shell = interactiveWorkflow.Shell;

            _statusBarViewModel = new ConnectionStatusBarViewModel(this, interactiveWorkflow.Shell);

            _disposableBag = DisposableBag.Create<ConnectionManager>()
                .Add(_statusBarViewModel)
                .Add(() => _sessionProvider.BrokerChanged -= BrokerChanged)
                .Add(() => interactiveWorkflow.RSession.Connected -= RSessionOnConnected)
                .Add(() => interactiveWorkflow.RSession.Disconnected -= RSessionOnDisconnected);

            _sessionProvider.BrokerChanged += BrokerChanged;

            // TODO: Temporary solution - need to separate RHost errors and network connection issues
            interactiveWorkflow.RSession.Connected += RSessionOnConnected;
            interactiveWorkflow.RSession.Disconnected += RSessionOnDisconnected;

            // Get initial values
            var userConnections = CreateConnectionList();
            _userConnections = new ConcurrentDictionary<Uri, IConnection>(userConnections);

            UpdateRecentConnections();
            CompleteInitializationAsync().DoNotWait();
        }

        private async Task CompleteInitializationAsync() {
            await _shell.SwitchToMainThreadAsync();
            _disposableBag.Add(_statusBar.AddItem(new ConnectionStatusBar {
                DataContext = _statusBarViewModel
            }));
            await SwitchBrokerToLastConnection();
        }

        public void Dispose() {
            _disposableBag.TryMarkDisposed();
        }

        public IConnectionManagerVisualComponent GetOrCreateVisualComponent(IConnectionManagerVisualComponentContainerFactory visualComponentContainerFactory, int instanceId = 0) {
            if (VisualComponent != null) {
                return VisualComponent;
            }

            VisualComponent = visualComponentContainerFactory.GetOrCreate(this, instanceId).Component;
            return VisualComponent;
        }

        public IConnection AddOrUpdateConnection(string name, string path, string rCommandLineArguments, bool isUserCreated) {
            var newConnection = new Connection(name, path, rCommandLineArguments, DateTime.Now, isUserCreated);
            var connection = _userConnections.AddOrUpdate(newConnection.Id, newConnection, (k, v) => UpdateConnectionFactory(v, newConnection));

            UpdateRecentConnections();
            return connection;
        }

        public IConnection GetOrAddConnection(string name, string path, string rCommandLineArguments, bool isUserCreated) {
            var newConnection = CreateConnection(name, path, rCommandLineArguments, isUserCreated);
            var connection = _userConnections.GetOrAdd(newConnection.Id, newConnection);
            UpdateRecentConnections();
            return connection;
        }

        public bool TryRemove(Uri id) {
            IConnection connection;
            var isRemoved = _userConnections.TryRemove(id, out connection);
            if (isRemoved) {
                UpdateRecentConnections();
            }

            return isRemoved;
        }

        public async Task ConnectAsync(IConnectionInfo connection) {
            if (ActiveConnection != null && (!ActiveConnection.Path.PathEquals(connection.Path) || string.IsNullOrEmpty(_sessionProvider.Broker.Name))) {
                await TrySwitchBrokerAsync(connection);
            }
        }

        public Task<bool> TrySwitchBrokerAsync(IConnectionInfo info) {
            var connection = GetOrCreateConnection(info.Name, info.Path, info.RCommandLineArguments, info.IsUserCreated);
            return TrySwitchBrokerAsync(connection);
        }

        private async Task<bool> TrySwitchBrokerAsync(IConnection connection) {
            var brokerSwitched = await _sessionProvider.TrySwitchBrokerAsync(connection.Name, connection.Path);
            if (brokerSwitched) {
                ActiveConnection = connection;
                SaveActiveConnectionToSettings();
            }
            return brokerSwitched;
        }

        private IConnection CreateConnection(string name, string path, string rCommandLineArguments, bool isUserCreated) =>
            new Connection(name, path, rCommandLineArguments, DateTime.Now, isUserCreated);

        private IConnection GetOrCreateConnection(string name, string path, string rCommandLineArguments, bool isUserCreated) {
            var newConnection = CreateConnection(name, path, rCommandLineArguments, isUserCreated);
            IConnection connection;
            return _userConnections.TryGetValue(newConnection.Id, out connection) ? connection : newConnection;
        }

        private IConnection UpdateConnectionFactory(IConnection oldConnection, IConnection newConnection) {
            if (oldConnection != null && newConnection.Equals(oldConnection)) {
                return oldConnection;
            }

            UpdateActiveConnection();
            return newConnection;
        }

        private Dictionary<Uri, IConnection> GetConnectionsFromSettings() => _settings.Connections
            .Select(c => CreateConnection(c.Name, c.Path, c.RCommandLineArguments, c.IsUserCreated))
            .ToDictionary(k => k.Id);

        private void SaveConnectionsToSettings() {
            _settings.Connections = RecentConnections
                .Select(c => new ConnectionInfo { Name = c.Name, Path = c.Path, RCommandLineArguments = c.RCommandLineArguments, IsUserCreated = c.IsUserCreated })
                .ToArray();
        }

        private void UpdateRecentConnections() {
            RecentConnections = new ReadOnlyCollection<IConnection>(_userConnections.Values.OrderByDescending(c => c.LastUsed).ToList());
            SaveConnectionsToSettings();
            RecentConnectionsChanged?.Invoke(this, new EventArgs());
        }

        private Dictionary<Uri, IConnection> CreateConnectionList() {
            var connections = GetConnectionsFromSettings();
            var localEngines = new RInstallation().GetCompatibleEngines();

            // Remove missing engines and add engines missing from saved connections
            // Set 'is used created' to false if path points to locally found interpreter
            foreach (var kvp in connections.Where(c => !c.Value.IsRemote).ToList()) {
                var valid = IsValidLocalConnection(kvp.Value.Name, kvp.Value.Path);
                if (!valid) {
                    connections.Remove(kvp.Key);
                }
            }

            // Add newly installed engines
            foreach (var e in localEngines) {
                if (!connections.Values.Any(x => x.Path.PathEquals(e.InstallPath))) {
                    connections[new Uri(e.InstallPath, UriKind.Absolute)] = CreateConnection(e.Name, e.InstallPath, string.Empty, isUserCreated: false);
                }
            }

            // Verify that most recently used connection is still valid
            var last = _settings.LastActiveConnection;
            if (last != null && !IsValidLocalConnection(last.Name, last.Path)) {
                _settings.LastActiveConnection = null;
            }

            if (connections.Count == 0) {
                if (!localEngines.Any()) {
                    var message = string.Format(CultureInfo.InvariantCulture, Resources.NoLocalR, Environment.NewLine + Environment.NewLine, Environment.NewLine);
                    if (_shell.ShowMessage(message, MessageButtons.YesNo) == MessageButtons.Yes) {
                        var installer = _shell.ExportProvider.GetExportedValue<IMicrosoftRClientInstaller>();
                        installer.LaunchRClientSetup(_shell);
                        return connections;
                    }
                }
                // No connections, may be first use or connections were removed.
                // Add local connections so there is at least something available.
                foreach (var e in localEngines) {
                    var c = CreateConnection(e.Name, e.InstallPath, string.Empty, isUserCreated: false);
                    connections[new Uri(e.InstallPath, UriKind.Absolute)] = c;
                }
            }
            return connections;
        }

        private bool IsValidLocalConnection(string name, string path) {
            try {
                var info = new RInterpreterInfo(name, path);
                return info.VerifyInstallation();
            } catch (Exception ex) when (!ex.IsCriticalException()) {
                GeneralLog.Write(ex);
            }
            return false;
        }

        private Task SwitchBrokerToLastConnection() {
            var connectionInfo = _settings.LastActiveConnection;
            if (!string.IsNullOrEmpty(connectionInfo?.Path)) {
                return TrySwitchBrokerAsync(connectionInfo);
            }

            var connection = RecentConnections.FirstOrDefault();
            if (connectionInfo != null) {
                return TrySwitchBrokerAsync(connection);
            }

            var local = _userConnections.Values.FirstOrDefault(c => !c.IsRemote);
            if (local != null) {
                return TrySwitchBrokerAsync(local);
            }

            return Task.CompletedTask;
        }

        private void BrokerChanged(object sender, EventArgs eventArgs) {
            UpdateActiveConnection();
        }

        private void RSessionOnConnected(object sender, RConnectedEventArgs e) {
            IsConnected = true;
            ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(true, ActiveConnection));
        }

        private void RSessionOnDisconnected(object sender, EventArgs e) {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(false, ActiveConnection));
        }

        private void UpdateActiveConnection() {
            if (ActiveConnection?.Id == _sessionProvider.Broker.Uri) {
                return;
            }

            ActiveConnection = RecentConnections.FirstOrDefault(c => c.Id == _sessionProvider.Broker.Uri);
            SaveActiveConnectionToSettings();
        }

        private void SaveActiveConnectionToSettings() {
            _settings.LastActiveConnection = ActiveConnection == null
                ? null
                : new ConnectionInfo {
                    Name = ActiveConnection.Name,
                    Path = ActiveConnection.Path,
                    RCommandLineArguments = ActiveConnection.RCommandLineArguments
                };
        }
    }
}