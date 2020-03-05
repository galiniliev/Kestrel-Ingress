using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Ingress.Library;

namespace Ingress.Controller
{
    internal class IngressHostedService : IHostedService
    {
        private readonly KubernetesClientConfiguration _config;
        private readonly ILogger<IngressHostedService> _logger;
        private Watcher<Extensionsv1beta1Ingress> _watcher;
        private Watcher<V1EndpointsList> _endpointWatcher;
        private Process _process;
        private Kubernetes _klient;
        private object _sync = new object();

        Dictionary<string, List<string>> _serviceToIp = new Dictionary<string, List<string>>();
        public IngressHostedService(KubernetesClientConfiguration config, ILogger<IngressHostedService> logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Started ingress hosted service!!!");
            try
            {
                _logger.LogInformation($"Using config: {_config.Host}, {_config.AccessToken}");

                _klient = new Kubernetes(_config);
                var result = await _klient.ListNamespacedIngressWithHttpMessagesAsync("ingress-test", watch: true);
                _watcher = result.Watch((Action<WatchEventType, Extensionsv1beta1Ingress>)(async (type, item) =>
                {
                    _logger.LogInformation("Received event");

                    if (type == WatchEventType.Added)
                    {
                        _logger.LogInformation("Added event");
                        await CreateJsonBlob(item);
                        StartProcess();
                    }
                    else if (type == WatchEventType.Deleted)
                    {
                        _logger.LogInformation("Deleted event");
                        _process.Close();
                    }
                    else if (type == WatchEventType.Modified)
                    {
                        // Generate a new configuration here and let the process handle it.
                        _logger.LogInformation("Modified event");
                        await CreateJsonBlob(item);
                    }
                    else
                    {
                        // Error, close the process?
                    }
                }));


                var result2 = await _klient.ListNamespacedEndpointsWithHttpMessagesAsync("ingress-test", watch: true);
                _endpointWatcher = result2.Watch((Action<WatchEventType, V1EndpointsList>)((type, item) =>
                {
                    _logger.LogInformation($"Got endpoints {type.ToString()}");

                    if (type == WatchEventType.Added)
                    {
                        UpdateServiceToEndpointDictionary(item);
                    }
                    else if (type == WatchEventType.Modified)
                    {
                        UpdateServiceToEndpointDictionary(item);
                    }
                    else if (type == WatchEventType.Deleted)
                    {
                        // remove from dictionary.
                    }
                }));

            }
            catch (Microsoft.Rest.HttpOperationException httpOperationException)
            {
                var phase = httpOperationException.Response.ReasonPhrase;
                //Bad Request 
                var content = httpOperationException.Response.Content;
                // {"error":{"code":"InvalidRequest","message":"Invalid dataset. This API can only be called on a DirectQuery dataset"}}
                _logger.LogError($"Call failed {phase}. Response message '{content}'");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Call failed {ex}");
            }
        }

        private void UpdateServiceToEndpointDictionary(V1EndpointsList item)
        {
            lock (_sync)
            {
                if (item != null && item.Items != null)
                {
                    foreach (var endpoint in item.Items)
                    {
                        _serviceToIp[endpoint.Metadata.Name] = endpoint.Subsets.SelectMany((o) => o.Addresses).Select(a => a.Ip).ToList();
                        _logger.LogInformation($"{endpoint.Metadata.Name} {_serviceToIp[endpoint.Metadata.Name].ToString()}");
                    }
                }
            }
        }

        private void StartProcess()
        {
            _process = new Process();
            _logger.LogInformation(File.Exists("/app/Ingress/Ingress.dll").ToString());
            var startInfo = new ProcessStartInfo("dotnet", "/app/Ingress/Ingress.dll");
            startInfo.WorkingDirectory = "/app/Ingress";
            startInfo.CreateNoWindow = true;
            _process.StartInfo = startInfo;
            _process.Start();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Nothing to stop
            _watcher.Dispose();
            return Task.CompletedTask;
        }

        private async ValueTask CreateJsonBlob(Extensionsv1beta1Ingress ingress)
        {
            // Get IP and port from k8s.
            var ingressFile = "/app/Ingress/ingress.json";

            var fileStream = File.Open(ingressFile, FileMode.Create);
            var ipMappingList = new List<IpMapping>();
            if (ingress.Spec.Backend != null)
            {
                // TODO
            }
            else
            {
                // TODO maybe check that a host is present:
                // An optional host. In this example, no host is specified, so the rule applies to all 
                // inbound HTTP traffic through the IP address specified. If a host is provided 
                // (for example, foo.bar.com), the rules apply to that host.
                foreach (var i in ingress.Spec.Rules)
                {
                    foreach (var path in i.Http.Paths)
                    {
                        bool exists;
                        List<string> ipList;

                        lock (_sync)
                        {
                            exists = _serviceToIp.TryGetValue(path.Backend.ServiceName, out ipList);
                            _logger.LogInformation(path.Backend.ServiceName);
                        }

                        if (exists)
                        {
                            _logger.LogInformation("IP mapping exists, use it.");

                            ipMappingList.Add(new IpMapping { IpAddresses = ipList, Port = path.Backend.ServicePort, Path = path.Path });
                        }
                        else
                        {
                            _logger.LogInformation("querying for endpoints");
                            var endpoints = await _klient.ListNamespacedEndpointsAsync(namespaceParameter: ingress.Metadata.NamespaceProperty);
                            var service = await _klient.ReadNamespacedServiceAsync(path.Backend.ServiceName, ingress.Metadata.NamespaceProperty);

                            // TODO can there be multiple ports here?
                            var targetPort = service.Spec.Ports.Where(e => e.Port == path.Backend.ServicePort).Select(e => e.TargetPort).Single();

                            UpdateServiceToEndpointDictionary(endpoints);
                            lock (_sync)
                            {
                                // From what it looks like, scheme is always http unless the tls section is specified, 
                                ipMappingList.Add(new IpMapping { IpAddresses = _serviceToIp[path.Backend.ServiceName], Port = targetPort, Path = path.Path, Scheme = "http" });
                            }
                        }
                    }
                }
            }

            var json = new IngressBindingOptions() { IpMappings = ipMappingList };
            await JsonSerializer.SerializeAsync(fileStream, json, typeof(IngressBindingOptions));
            fileStream.Close();
        }
    }
}