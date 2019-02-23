﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using csv_prometheus_exporter.Parser;
using csv_prometheus_exporter.Prometheus;
using csv_prometheus_exporter.Scraper.Config;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Web;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using Environment = System.Environment;
using ILogger = NLog.ILogger;
using LogLevel = NLog.LogLevel;

namespace csv_prometheus_exporter.Scraper
{
    internal static class Scraper
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private static ScraperConfig ReadCoreConfig(out IList<ColumnReader> readers)
        {
            var scrapeConfigFilename = Environment.GetEnvironmentVariable("SCRAPECONFIG") ?? "/etc/scrapeconfig.yml";

            var config = new DeserializerBuilder()
                .Build()
                .Deserialize<ScraperConfig>(new StreamReader(scrapeConfigFilename));

            LoadReadersConfig(config.Global, out readers);

            return config;
        }

        private static void LoadFromScript(IDictionary<string, SSHLogScraper> scrapers, string scrapeConfigScript,
            IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
        {
            var split = scrapeConfigScript.Split(' ');
            var startInfo = new ProcessStartInfo
            {
                FileName = split[0],
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in split.Skip(1)) startInfo.ArgumentList.Add(arg);

            var process = new Process {StartInfo = startInfo};
            process.Start();
            process.WaitForExit();
            var config = new DeserializerBuilder()
                .Build()
                .Deserialize<ScraperConfig>(process.StandardOutput);
            LoadScrapersConfig(scrapers, config, readers, metrics);
        }

        private static HashSet<string> LoadSshScrapersConfig(IDictionary<string, SSHLogScraper> scrapers,
            SSH config,
            IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
        {
            var ids = new HashSet<string>();
            foreach (var (envName, envConfig) in config.Environments)
            {
                foreach (var host in envConfig.Hosts)
                {
                    var targetId = $"ssh://{host}/{envConfig.File ?? config.File}";
                    ids.Add(targetId);
                    if (scrapers.ContainsKey(targetId))
                        continue;

                    var scraper = new SSHLogScraper(
                        envConfig.File ?? config.File,
                        envName,
                        readers,
                        host,
                        envConfig.User ?? config.User,
                        envConfig.Password ?? config.Password,
                        envConfig.PKey ?? config.PKey,
                        envConfig.ConnectTimeout ?? config.ConnectTimeout ?? 30,
                        metrics
                    );
                    scraper.Thread = new Thread(() => scraper.Run());
                    scrapers[targetId] = scraper;
                    Startup.Scrapers[targetId] = scraper;
                    scraper.Thread.IsBackground = true;
                    scraper.Thread.Name = "scraper:" + targetId;
                    scraper.Thread.Start();
                }
            }

            return ids;
        }

        private static void LoadReadersConfig(Global globalConfig, out IList<ColumnReader> readers)
        {
            readers = new List<ColumnReader>();
            var histogramBuckets = new Dictionary<string, double[]>();
            if (globalConfig.Histograms != null)
                foreach (var (histogramName, buckets) in globalConfig.Histograms)
                {
                    if (buckets == null || buckets.Count == 0)
                        histogramBuckets[histogramName] =
                            Histogram.DefaultBuckets;
                    else
                        histogramBuckets[histogramName] =
                            buckets.Cast<YamlScalarNode>().Select(x => double.Parse(x.Value)).ToArray();
                }

            Startup.Metrics.Clear();
            foreach (var dict in globalConfig.Format)
            {
                if (dict == null)
                {
                    readers.Add(null);
                    continue;
                }
                
                if (dict.Count != 1)
                    throw new Exception();

                var name = dict.First().Key;
                var typeDef = dict.First().Value;
                switch (typeDef)
                {
                    case null:
                        readers.Add(null);
                        continue;
                    case "label" when name == "environment":
                        throw new Exception("'environment' is a reserved label name");
                }

                if (typeDef != "label" && (name == "parser_errors" || name == "lines_parsed"))
                    throw new Exception($"'{name}' is a reserved metric name");

                var type = typeDef;
                if (typeDef.Contains('+'))
                {
                    var typeAndHistogram = typeDef.Split('+');
                    type = typeAndHistogram[0].Trim();
                    if (type == "label")
                        throw new Exception("Labels cannot be used as histograms");
                    var histogramType = typeAndHistogram[1].Trim();
                    if (!histogramBuckets.ContainsKey(histogramType))
                        throw new Exception($"Histogram type {histogramType} is not defined");
                    Startup.Metrics[name] = new MetricBase(name, $"Histogram of {name}", MetricsType.Histogram,
                        histogramBuckets[histogramType]);
                }
                else if (type != "label" && type != "request_header")
                {
                    Startup.Metrics[name] = new MetricBase(name, $"Sum of {name}", MetricsType.Counter);
                }

                readers.Add(ColumnReaders.Create(type, name));
            }

            MetricBase.GlobalPrefix = globalConfig.Prefix;
            MetricBase.TTL = globalConfig.TTL;

            Startup.Metrics["parser_errors"] =
                new MetricBase("parser_errors", "Number of lines which could not be parsed", MetricsType.Counter);
            Startup.Metrics["lines_parsed"] =
                new MetricBase("lines_parsed", "Number of successfully parsed lines", MetricsType.Counter);
            Startup.Metrics["connected"] =
                new MetricBase("connected", "Whether this target is currently being scraped", MetricsType.Gauge, null,
                    true);
        }

        private static void LoadScrapersConfig(IDictionary<string, SSHLogScraper> scrapers, ScraperConfig scrapeConfig,
            IList<ColumnReader> readers, IDictionary<string, MetricBase> metrics)
        {
            var loadedIds = LoadSshScrapersConfig(scrapers, scrapeConfig.SSH, readers, metrics);
            foreach (var (scraperId, scraper) in scrapers)
                if (!loadedIds.Contains(scraperId))
                    scraper.CancellationTokenSource.Cancel();
        }

        private static void InitLogging()
        {
            var config = new LoggingConfiguration();
            var console = new ColoredConsoleTarget("console");
            config.AddTarget(console);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, console);
            LogManager.Configuration = config;
        }

        private static void Main(string[] args)
        {
            InitLogging();
            ServicePointManager.DefaultConnectionLimit = 1;

            ThreadPool.GetMinThreads(out var a, out var b);
            logger.Debug($"Current min threads: {a}, {b}");
            if (!ThreadPool.SetMinThreads(1024, 128))
                throw new Exception("Failed to set minimum thread count");

            var scrapeConfig = ReadCoreConfig(out var readers);
            var scrapers = new Dictionary<string, SSHLogScraper>();
            LoadScrapersConfig(scrapers, scrapeConfig, readers, Startup.Metrics);

            if (scrapeConfig.Script != null)
            {
                var loaderThread = new Thread(() =>
                {
                    while (true)
                    {
                        LoadFromScript(scrapers, scrapeConfig.Script, readers, Startup.Metrics);

                        if (scrapeConfig.ReloadInterval.HasValue)
                            Thread.Sleep(TimeSpan.FromSeconds(scrapeConfig.ReloadInterval.Value));
                        else
                            Thread.Sleep(-1);
                    }

                    // ReSharper disable once FunctionNeverReturns
                }) {Name = "inventory-loader-thread"};

                loaderThread.Start();
            }

            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseKestrel(options => { options.ListenAnyIP(5000); })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                })
                .UseNLog()
                .Build()
                .Run();
            // ReSharper disable once FunctionNeverReturns
        }
    }
}
