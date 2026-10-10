using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Diary.Survey;

public sealed class ExtendedSurveyServiceRegistry
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public IDisposable Register(ExtendedSurveyMcpService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return Register(() => service);
    }

    public IDisposable Register(Func<ExtendedSurveyMcpService> serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var initialService = serviceProvider();
        if (string.IsNullOrWhiteSpace(initialService.ServiceId))
            throw new ArgumentException("MCP 服务 ID 不能为空。", nameof(serviceProvider));

        var registration = new Registration(this, initialService.ServiceId, serviceProvider);
        lock (_sync)
            _registrations[initialService.ServiceId] = registration;
        return registration;
    }

    public IReadOnlyList<ExtendedSurveyMcpService> Snapshot()
    {
        lock (_sync)
            return _registrations.Values.Select(item => item.GetService()).ToArray();
    }

    private void Unregister(Registration registration)
    {
        lock (_sync)
        {
            if (_registrations.TryGetValue(registration.ServiceId, out var current)
                && ReferenceEquals(current, registration))
            {
                _registrations.Remove(registration.ServiceId);
            }
        }
    }

    private sealed class Registration(
        ExtendedSurveyServiceRegistry owner,
        string serviceId,
        Func<ExtendedSurveyMcpService> serviceProvider) : IDisposable
    {
        private int _disposed;

        public string ServiceId { get; } = serviceId;

        public ExtendedSurveyMcpService GetService()
        {
            var service = serviceProvider();
            if (!string.Equals(service.ServiceId, ServiceId, StringComparison.Ordinal))
                throw new InvalidOperationException("MCP 服务提供器不能在注册后更改 service_id。");
            return service;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unregister(this);
        }
    }
}

public sealed record DiscoveredExtendedSurveyMcpService(
    string InstanceId,
    string Hostname,
    string Username,
    ExtendedSurveyMcpService Service,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed class ExtendedSurveyServiceDirectory
{
    private readonly Dictionary<string, DiscoveredExtendedSurveyMcpService> _services = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public event EventHandler? Changed;

    public IReadOnlyList<DiscoveredExtendedSurveyMcpService> Snapshot()
    {
        lock (_sync)
            return _services.Values.OrderBy(item => item.Hostname).ThenBy(item => item.Service.ServiceId).ToArray();
    }

    public void Apply(ExtendedSurveyMcpServices response, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        var changed = false;
        lock (_sync)
        {
            var receivedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var service in response.Services.Where(IsSupported))
            {
                var key = CreateKey(response.InstanceId, service.ServiceId);
                receivedKeys.Add(key);
                var ttl = TimeSpan.FromSeconds(Math.Clamp(service.ExpiresInSeconds, 15, 300));
                var discovered = new DiscoveredExtendedSurveyMcpService(
                    response.InstanceId,
                    response.Hostname,
                    response.Username,
                    service,
                    nowUtc,
                    nowUtc + ttl);
                if (!_services.TryGetValue(key, out var previous))
                {
                    _services[key] = discovered;
                    changed = true;
                }
                else if (HasMeaningfulChange(previous, discovered))
                {
                    _services[key] = discovered;
                    changed = true;
                }
                else
                {
                    _services[key] = discovered;
                }
            }

            foreach (var staleKey in _services
                         .Where(item => item.Value.InstanceId == response.InstanceId && !receivedKeys.Contains(item.Key))
                         .Select(item => item.Key)
                         .ToArray())
            {
                _services.Remove(staleKey);
                changed = true;
            }
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public int Prune(DateTimeOffset nowUtc)
    {
        int removed;
        lock (_sync)
        {
            var expired = _services
                .Where(item => item.Value.ExpiresAtUtc <= nowUtc)
                .Select(item => item.Key)
                .ToArray();
            foreach (var key in expired)
                _services.Remove(key);
            removed = expired.Length;
        }

        if (removed > 0)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private static bool IsSupported(ExtendedSurveyMcpService service) =>
        string.Equals(
            service.Transport,
            ExtendedSurveyProtocol.StreamableHttpTransport,
            StringComparison.Ordinal)
        && service.Endpoints is not null
        && service.Endpoints.Any(endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https");

    private static bool HasMeaningfulChange(
        DiscoveredExtendedSurveyMcpService previous,
        DiscoveredExtendedSurveyMcpService current) =>
        !string.Equals(previous.Hostname, current.Hostname, StringComparison.Ordinal)
        || !string.Equals(previous.Username, current.Username, StringComparison.Ordinal)
        || !string.Equals(previous.Service.DisplayName, current.Service.DisplayName, StringComparison.Ordinal)
        || !string.Equals(previous.Service.Transport, current.Service.Transport, StringComparison.Ordinal)
        || previous.Service.StartedAt != current.Service.StartedAt
        || !(previous.Service.Endpoints ?? []).SequenceEqual(
            current.Service.Endpoints ?? [],
            StringComparer.Ordinal)
        || !(previous.Service.Capabilities ?? []).SequenceEqual(
            current.Service.Capabilities ?? [],
            StringComparer.Ordinal);

    private static string CreateKey(string instanceId, string serviceId) => $"{instanceId}\n{serviceId}";
}

public sealed class SurveyMcpPeerAccessPolicy(
    Func<string?> investigatorAddressProvider,
    Func<IReadOnlyCollection<IPAddress>>? localAddressProvider = null)
{
    private readonly Func<IReadOnlyCollection<IPAddress>> _localAddressProvider =
        localAddressProvider ?? GetLocalAddresses;

    public bool IsAllowed(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
            return false;
        var normalized = Normalize(remoteAddress);
        if (IPAddress.IsLoopback(normalized))
            return true;
        if (_localAddressProvider().Select(Normalize).Contains(normalized))
            return true;

        var configured = investigatorAddressProvider()?.Trim();
        return IPAddress.TryParse(configured, out var investigator)
            && Normalize(investigator).Equals(normalized);
    }

    public IReadOnlyList<IPAddress> GetAdvertisableAddresses()
    {
        var addresses = GetLocalAddresses()
            .Select(Normalize)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Where(address => !IPAddress.IsLoopback(address))
            .Distinct()
            .ToList();
        var preferred = ResolvePreferredAddress();
        if (preferred is not null && addresses.Remove(preferred))
            addresses.Insert(0, preferred);
        addresses.Add(IPAddress.Loopback);
        return addresses;
    }

    private IPAddress? ResolvePreferredAddress()
    {
        if (!IPAddress.TryParse(investigatorAddressProvider()?.Trim(), out var investigator))
            return null;
        try
        {
            using var socket = new Socket(investigator.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(investigator, SurveyPorts.Extended));
            return (socket.LocalEndPoint as IPEndPoint)?.Address is { } address
                ? Normalize(address)
                : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static IReadOnlyCollection<IPAddress> GetLocalAddresses() => NetworkInterface
        .GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .ToArray();

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
