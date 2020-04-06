﻿namespace NServiceBus.Transport.RabbitMQ
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Support;

    class ConnectionConfiguration
    {
        const bool defaultUseTls = false;
        const int defaultPort = 5672;
        const int defaultTlsPort = 5671;
        const string defaultVirtualHost = "/";
        const string defaultUserName = "guest";
        const string defaultPassword = "guest";
        const ushort defaultRequestedHeartbeat = 60;
        static readonly TimeSpan defaultRetryDelay = TimeSpan.FromSeconds(10);
        const string defaultCertPath = "";
        const string defaultCertPassphrase = null;

        public string Host { get; }

        public int Port { get; }

        public string VirtualHost { get; }

        public string UserName { get; }

        public string Password { get; }

        public ushort RequestedHeartbeat { get; }

        public TimeSpan RetryDelay { get; }

        public bool UseTls { get; }

        public string CertPath { get; }

        public string CertPassphrase { get; }

        public Dictionary<string, string> ClientProperties { get; }

        ConnectionConfiguration(
            string host,
            int port,
            string virtualHost,
            string userName,
            string password,
            ushort requestedHeartbeat,
            TimeSpan retryDelay,
            bool useTls,
            string certPath,
            string certPassphrase,
            Dictionary<string, string> clientProperties)
        {
            Host = host;
            Port = port;
            VirtualHost = virtualHost;
            UserName = userName;
            Password = password;
            RequestedHeartbeat = requestedHeartbeat;
            RetryDelay = retryDelay;
            UseTls = useTls;
            CertPath = certPath;
            CertPassphrase = certPassphrase;
            ClientProperties = clientProperties;
        }

        public static ConnectionConfiguration Create(string connectionString, string endpointName)
        {
            Dictionary<string, string> dictionary;
            var invalidOptionsMessage = new StringBuilder();
            var host = default(string);
            int port;

            if (connectionString.StartsWith("amqp", StringComparison.OrdinalIgnoreCase))
            {
                dictionary = new Dictionary<string, string>();

                var uri = new Uri(connectionString);

                var usingTls = string.Equals("amqps", uri.Scheme, StringComparison.OrdinalIgnoreCase) ? bool.TrueString : bool.FalseString;
                dictionary.Add("useTls", usingTls);

                if (!string.IsNullOrEmpty(uri.Host))
                {
                    host = uri.Host;
                }

                if (!uri.IsDefaultPort)
                {
                    dictionary.Add("port", uri.Port.ToString());
                }

                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var userPass = uri.UserInfo.Split(':');
                    if (userPass.Length > 2)
                    {
                        invalidOptionsMessage.AppendLine($"Bad user info in AMQP URI: {uri.UserInfo}");
                    }
                    else
                    {
                        dictionary.Add("userName", UriDecode(userPass[0]));

                        if (userPass.Length == 2)
                        {
                            dictionary.Add("password", UriDecode(userPass[1]));
                        }
                    }
                }

                if (uri.Segments.Length > 2)
                {
                    invalidOptionsMessage.AppendLine($"Multiple segments in path of AMQP URI: {string.Join(", ", uri.Segments)}");
                }
                else if (uri.Segments.Length == 2)
                {
                    dictionary.Add("virtualHost", UriDecode(uri.Segments[1]));
                }
            }
            else // non-AMQP connection string
            {
                dictionary = new DbConnectionStringBuilder {ConnectionString = connectionString}
                    .OfType<KeyValuePair<string, object>>()
                    .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                if (dictionary.ContainsKey("dequeuetimeout"))
                {
                    invalidOptionsMessage.AppendLine("The 'DequeueTimeout' connection string option has been removed. Consult the documentation for further information.");
                }

                if (dictionary.ContainsKey("maxwaittimeforconfirms"))
                {
                    invalidOptionsMessage.AppendLine("The 'MaxWaitTimeForConfirms' connection string option has been removed. Consult the documentation for further information.");
                }

                if (dictionary.ContainsKey("prefetchcount"))
                {
                    invalidOptionsMessage.AppendLine("The 'PrefetchCount' connection string option has been removed. Use 'EndpointConfiguration.UseTransport<RabbitMQTransport>().PrefetchCount' instead.");
                }

                if (dictionary.ContainsKey("usepublisherconfirms"))
                {
                    invalidOptionsMessage.AppendLine("The 'UsePublisherConfirms' connection string option has been removed. Use 'EndpointConfiguration.UseTransport<RabbitMQTransport>().UsePublisherConfirms' instead.");
                }

                if (dictionary.TryGetValue("port", out var portValue) && !int.TryParse(portValue, out port))
                {
                    invalidOptionsMessage.AppendLine($"'{portValue}' is not a valid Int32 value for the 'port' connection string option.");
                }

                if (dictionary.TryGetValue("host", out var value))
                {
                    var hostsAndPorts = value.Split(',');

                    if (hostsAndPorts.Length > 1)
                    {
                        invalidOptionsMessage.AppendLine("Multiple hosts are no longer supported. If using RabbitMQ in a cluster, consider using a load balancer to represent the nodes as a single host.");
                    }

                    var parts = hostsAndPorts[0].Split(':');
                    host = parts.ElementAt(0);

                    if (host.Length == 0)
                    {
                        invalidOptionsMessage.AppendLine("Empty host name in 'host' connection string option.");
                    }

                    if (parts.Length > 1)
                    {
                        if (!int.TryParse(parts[1], out port))
                        {
                            invalidOptionsMessage.AppendLine($"'{parts[1]}' is not a valid Int32 value for the port in the 'host' connection string option.");
                        }
                        else
                        {
                            // Override the port with the value provided via host ("host=hostname:portnumber")
                            dictionary["port"] = port.ToString();
                        }
                    }
                }
                else
                {
                    invalidOptionsMessage.AppendLine("Invalid connection string. 'host' value must be supplied. e.g: \"host=myServer\"");
                }
            }

            var useTls = GetValue(dictionary, "useTls", bool.TryParse, defaultUseTls, invalidOptionsMessage);
            port = GetValue(dictionary, "port", int.TryParse, useTls ? defaultTlsPort : defaultPort, invalidOptionsMessage);
            var virtualHost = GetValue(dictionary, "virtualHost", defaultVirtualHost);
            var userName = GetValue(dictionary, "userName", defaultUserName);
            var password = GetValue(dictionary, "password", defaultPassword);
            var requestedHeartbeat = GetValue(dictionary, "requestedHeartbeat", ushort.TryParse, defaultRequestedHeartbeat, invalidOptionsMessage);
            var retryDelay = GetValue(dictionary, "retryDelay", TimeSpan.TryParse, defaultRetryDelay, invalidOptionsMessage);
            var certPath = GetValue(dictionary, "certPath", defaultCertPath);
            var certPassPhrase = GetValue(dictionary, "certPassphrase", defaultCertPassphrase);

            if (invalidOptionsMessage.Length > 0)
            {
                throw new NotSupportedException(invalidOptionsMessage.ToString().TrimEnd('\r', '\n'));
            }

            var nsbVersion = FileVersionInfo.GetVersionInfo(typeof(Endpoint).Assembly.Location);
            var nsbFileVersion = $"{nsbVersion.FileMajorPart}.{nsbVersion.FileMinorPart}.{nsbVersion.FileBuildPart}";

            var rabbitMQVersion = FileVersionInfo.GetVersionInfo(typeof(ConnectionConfiguration).Assembly.Location);
            var rabbitMQFileVersion = $"{rabbitMQVersion.FileMajorPart}.{rabbitMQVersion.FileMinorPart}.{rabbitMQVersion.FileBuildPart}";

            var applicationNameAndPath = Environment.GetCommandLineArgs()[0];
            var applicationName = Path.GetFileName(applicationNameAndPath);
            var applicationPath = Path.GetDirectoryName(applicationNameAndPath);

            var hostname = RuntimeEnvironment.MachineName;

            var clientProperties = new Dictionary<string, string>
            {
                { "client_api", "NServiceBus" },
                { "nservicebus_version", nsbFileVersion },
                { "nservicebus.rabbitmq_version", rabbitMQFileVersion },
                { "application", applicationName },
                { "application_location", applicationPath },
                { "machine_name", hostname },
                { "user", userName },
                { "endpoint_name", endpointName },
            };

            return new ConnectionConfiguration(
                host, port, virtualHost, userName, password, requestedHeartbeat, retryDelay, useTls, certPath, certPassPhrase, clientProperties);
        }

        static string GetValue(Dictionary<string, string> dictionary, string key, string defaultValue)
        {
            return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
        }

        static T GetValue<T>(Dictionary<string, string> dictionary, string key, Convert<T> convert, T defaultValue, StringBuilder invalidOptionsMessage)
        {
            if (dictionary.TryGetValue(key, out var value))
            {
                if (!convert(value, out defaultValue))
                {
                    invalidOptionsMessage.AppendLine($"'{value}' is not a valid {typeof(T).Name} value for the '{key}' connection string option.");
                }
            }

            return defaultValue;
        }

        delegate bool Convert<T>(string input, out T output);

        static string UriDecode(string value)
        {
            return Uri.UnescapeDataString(value.Replace("+", "%2B"));
        }
    }
}
