using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace CounterSnapshot
{
	class Program
	{
		static void ShowHelp()
		{
			Console.WriteLine("Usage:");
			Console.WriteLine("  dotnet-counters-snapshot [options] [command]");
			Console.WriteLine();
			Console.WriteLine("Options:");
			Console.WriteLine("  --version    Display version information");
			Console.WriteLine();
			Console.WriteLine("Commands:");
			Console.WriteLine("  help");
			Console.WriteLine("    This help");
			Console.WriteLine("  snapshot (-p|--process-id) <pid> [(-m|--max-time) <period_in_ms>] <counter_list[]>");
			Console.WriteLine("    Collects a snapshot of each counter in the counter_list, then exits");
			Console.WriteLine();
		}

		static void Main(string[] args)
		{
			if (args.Length == 0 || args[0] == "help" || (args[0] != "--version" && args[0] != "snapshot"))
			{
				ShowHelp();
				return;
			}
			if (args[0] == "--version")
			{
				Console.WriteLine(Assembly.GetCallingAssembly().GetName().Version.ToString());
				Console.WriteLine();
				return;
			}

			//parse args 
			var countersToCollect = new Dictionary<string, List<string>>();
			var inCounters = false;
			int? pid = null;
			var maxTime = TimeSpan.FromMilliseconds(1000);
			for (var i = 1; i < args.Length; ++i)
			{
				if (!inCounters)
				{
					switch (args[i])
					{
						case "-p":
						case "--process-id":
							pid = int.Parse(args[++i], CultureInfo.CurrentCulture);
							continue;
						case "-m":
						case "--max-time":
							maxTime = TimeSpan.FromMilliseconds(double.Parse(args[++i], CultureInfo.CurrentCulture));
							continue;
						default:
							inCounters = true;
							break;
					}
				}

				if (!Regex.IsMatch(args[i], @"^\w\S+\w\[[\w\-]+(,[\w\-]+)*\]$"))
				{
					ShowHelp();
					return;
				}

				var delimiter = args[i].IndexOf('[', StringComparison.CurrentCulture);
				var provider = args[i].Substring(0, delimiter);
				var counters = args[i].Substring(delimiter + 1, args[i].Length - delimiter - 2).Split(',');
				if (countersToCollect.ContainsKey(provider))
				{
					countersToCollect[provider].AddRange(counters);
				}
				else
				{
					countersToCollect.Add(provider, new List<string>(counters));
				}
			}

			if (!pid.HasValue || !countersToCollect.Any())
			{
                ShowHelp();
				return;
			}

			//now collect the counters
			var pipelineArguments = new Dictionary<string, string> {{"EventCounterIntervalSec", "1"}};
			var providers = countersToCollect.Select(z => new EventPipeProvider(z.Key, EventLevel.Informational, (long) ClrTraceEventParser.Keywords.None, pipelineArguments));
			var client = new DiagnosticsClient(pid.Value);
			using (var session = client.StartEventPipeSession(providers, false))
			{
				var cancellationSource = new CancellationTokenSource();
                var getCounters = Task.Run(() =>
				{
					var source = new EventPipeEventSource(session.EventStream);
					source.Dynamic.All += (traceEvent =>
					{
						if (traceEvent.EventName == "EventCounters")
						{
							var provider = traceEvent.ProviderName;
							var metricInfo = ((traceEvent.PayloadValue(0) as IDictionary<string, object>)["Payload"] as IDictionary<string, object>);
							var metricName = metricInfo["Name"].ToString();
							var metricValue = double.Parse((metricInfo["Mean"] ?? metricInfo["Increment"]).ToString());
							lock (countersToCollect)
							{
								if (countersToCollect.ContainsKey(provider) && countersToCollect[provider].Contains(metricName))
								{
									Console.WriteLine($"{provider}/{metricName} {metricValue}");
									if (countersToCollect[provider].Count == 1)
									{
										if (countersToCollect.Count == 1)
										{
											//NOTE: we have to do this, else we end up getting the last counter multiple times
											countersToCollect.Clear();
											cancellationSource.Cancel();
										}
										else
										{
											countersToCollect.Remove(provider);
										}
									}
									else
									{
										countersToCollect[provider].Remove(metricName);
									}
								}
							}
						}
					});
					source.Process();
				}, cancellationSource.Token);
				var waitTask = Task.Delay(maxTime, cancellationSource.Token);
				Task.WaitAny(getCounters, waitTask);
				session.Stop();
			}
		}

    }
}
