using System.Diagnostics.Tracing;

namespace TreeSpitter.Benchmarks;

/// <summary>
/// Finds what the parses allocate: samples the allocations of the runtime (an event per 100 KB allocated, with the
/// type of the object) while the generated parser parses the input of a grammar.
/// </summary>
internal static class AllocationProbe
{
    public static void Run(string language, int size, int parses)
    {
        var parser = new Parser(Grammars.GeneratedLanguage(language));
        var input = System.Text.Encoding.UTF8.GetBytes(Grammars.Input(language, size));
        for (var i = 0; i < 5; i++)
        {
            parser.Parse(input).Dispose();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        using (var listener = new AllocationListener())
        {
            for (var i = 0; i < parses; i++)
            {
                parser.Parse(input).Dispose();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Thread.Sleep(500);
            Console.WriteLine($"{language}: {allocated / parses / 1024.0:F1} KB per parse");
            lock (listener.Types)
            {
                foreach (var (type, amount) in listener.Types.OrderByDescending(t => t.Value).Take(10))
                {
                    Console.WriteLine($"  {amount / parses / 1024.0,10:F1} KB  {type}");
                }
            }
        }
    }

    private sealed class AllocationListener : EventListener
    {
        public Dictionary<string, long> Types { get; } = [];

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                // The GC keyword, at the verbose level: the allocation ticks
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || eventData.Payload is null || eventData.PayloadNames is null)
            {
                return;
            }

            var type = eventData.Payload[eventData.PayloadNames.IndexOf("TypeName")] as string ?? "?";
            var amount = Convert.ToInt64(eventData.Payload[eventData.PayloadNames.IndexOf("AllocationAmount64")], System.Globalization.CultureInfo.InvariantCulture);
            lock (Types)
            {
                Types[type] = Types.GetValueOrDefault(type) + amount;
            }
        }
    }
}
