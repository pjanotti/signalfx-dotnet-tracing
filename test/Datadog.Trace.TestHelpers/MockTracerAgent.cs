// Modified by SignalFx
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Datadog.Trace.ExtensionMethods;
using MessagePack;

namespace Datadog.Trace.TestHelpers
{
    public class MockTracerAgent : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private readonly CancellationTokenSource _listenerCts = new CancellationTokenSource();

        public MockTracerAgent(int port = 8126, int retries = 5)
        {
            // try up to 5 consecutive ports before giving up
            while (true)
            {
                // seems like we can't reuse a listener if it fails to start,
                // so create a new listener each time we retry
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");

                try
                {
                    listener.Start();

                    // successfully listening
                    Port = port;
                    _listener = listener;

                    _listenerThread = new Thread(HandleHttpRequests);
                    _listenerThread.Start();

                    return;
                }
                catch (HttpListenerException) when (retries > 0)
                {
                    // only catch the exception if there are retries left
                    listener.Prefixes.Clear();
                    port = TcpPortProvider.GetOpenPort();
                    retries--;
                }

                // always close listener if exception is thrown,
                // whether it was caught or not
                listener.Close();
            }
        }

        public event EventHandler<EventArgs<HttpListenerContext>> RequestReceived;

        public event EventHandler<EventArgs<IList<IList<IMockSpan>>>> RequestDeserialized;

        /// <summary>
        /// Gets or sets a value indicating whether to skip serialization of traces.
        /// </summary>
        public bool ShouldDeserializeTraces { get; set; } = true;

        /// <summary>
        /// Gets the TCP port that this Agent is listening on.
        /// Can be different from <see cref="MockTracerAgent(int, int)"/>'s <c>initialPort</c>
        /// parameter if listening on that port fails.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// Gets the filters used to filter out spans we don't want to look at for a test.
        /// </summary>
        public List<Func<IMockSpan, bool>> SpanFilters { get; private set; } = new List<Func<IMockSpan, bool>>();

        public IImmutableList<IMockSpan> Spans { get; private set; } = ImmutableList<IMockSpan>.Empty;

        public IImmutableList<NameValueCollection> RequestHeaders { get; private set; } = ImmutableList<NameValueCollection>.Empty;

        /// <summary>
        /// Wait for the given number of spans to appear.
        /// </summary>
        /// <param name="count">The expected number of spans.</param>
        /// <param name="timeoutInMilliseconds">The timeout</param>
        /// <param name="operationName">The integration we're testing</param>
        /// <param name="minDateTime">Minimum time to check for spans from</param>
        /// <param name="returnAllOperations">When true, returns every span regardless of operation name</param>
        /// <returns>The list of spans.</returns>
        public IImmutableList<IMockSpan> WaitForSpans(
            int count,
            int timeoutInMilliseconds = 20000,
            string operationName = null,
            DateTimeOffset? minDateTime = null,
            bool returnAllOperations = false)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutInMilliseconds);
            var minimumOffset = (minDateTime ?? DateTimeOffset.MinValue).ToUnixTimeNanoseconds();

            IImmutableList<IMockSpan> relevantSpans = ImmutableList<IMockSpan>.Empty;

            while (DateTime.Now < deadline)
            {
                relevantSpans =
                    Spans
                       .Where(s => SpanFilters.All(shouldReturn => shouldReturn(s)))
                       .Where(s => s.Start > minimumOffset)
                       .ToImmutableList();

                if (relevantSpans.Count(s => operationName == null || s.Name == operationName) >= count)
                {
                    break;
                }

                Thread.Sleep(500);
            }

            foreach (var headers in RequestHeaders)
            {
                // This is the place to check against headers we expect
                AssertHeader(
                    headers,
                    "X-Datadog-Trace-Count",
                    header =>
                    {
                        if (int.TryParse(header, out int traceCount))
                        {
                            return traceCount > 0;
                        }

                        return false;
                    });
            }

            if (!returnAllOperations)
            {
                relevantSpans =
                    relevantSpans
                       .Where(s => operationName == null || s.Name == operationName)
                       .ToImmutableList();
            }

            return relevantSpans;
        }

        public void Dispose()
        {
            lock (_listener)
            {
                _listenerCts.Cancel();
                _listener.Stop();
            }
        }

        protected virtual void OnRequestReceived(HttpListenerContext context)
        {
            RequestReceived?.Invoke(this, new EventArgs<HttpListenerContext>(context));
        }

        protected virtual void OnRequestDeserialized(IList<IList<IMockSpan>> traces)
        {
            RequestDeserialized?.Invoke(this, new EventArgs<IList<IList<IMockSpan>>>(traces));
        }

        private void AssertHeader(
            NameValueCollection headers,
            string headerKey,
            Func<string, bool> assertion)
        {
            var header = headers.Get(headerKey);

            if (string.IsNullOrEmpty(header))
            {
                throw new Exception($"Every submission to the agent should have a {headerKey} header.");
            }

            if (!assertion(header))
            {
                throw new Exception($"Failed assertion for {headerKey} on {header}");
            }
        }

        private void HandleHttpRequests()
        {
            while (true)
            {
                try
                {
                    var getCtxTask = Task.Run(() => _listener.GetContext());
                    getCtxTask.Wait(_listenerCts.Token);

                    var ctx = getCtxTask.Result;
                   OnRequestReceived(ctx);

                    if (ShouldDeserializeTraces)
                    {
                        var dspans = MessagePackSerializer.Deserialize<List<List<Span>>>(ctx.Request.InputStream);
                        List<IList<IMockSpan>> spans = dspans.ConvertAll(x => (IList<IMockSpan>)x.ConvertAll(y => (IMockSpan)y));
                        OnRequestDeserialized((IList<IList<IMockSpan>>)spans);

                        lock (this)
                        {
                            // we only need to lock when replacing the span collection,
                            // not when reading it because it is immutable
                            Spans = Spans.AddRange(spans.SelectMany(trace => trace));
                            RequestHeaders = RequestHeaders.Add(new NameValueCollection(ctx.Request.Headers));
                        }
                    }

                    ctx.Response.ContentType = "application/json";
                    var buffer = Encoding.UTF8.GetBytes("{}");
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException || ex is OperationCanceledException || ex is AggregateException)
                {
                    lock (_listener)
                    {
                        if (!_listener.IsListening)
                        {
                            return;
                        }
                    }

                    throw;
                }
            }
        }

        [MessagePackObject]
        [DebuggerDisplay("TraceId={TraceId}, SpanId={SpanId}, Service={Service}, Name={Name}, Resource={Resource}")]
        public struct Span : IMockSpan
        {
            [Key("trace_id")]
            public ulong TraceId { get; set; }

            [Key("span_id")]
            public ulong SpanId { get; set; }

            [Key("name")]
            public string Name { get; set; }

            [Key("resource")]
            public string Resource { get; set; }

            [Key("service")]
            public string Service { get; set; }

            [Key("type")]
            public string Type { get; set; }

            [Key("start")]
            public long Start { get; set; }

            [Key("duration")]
            public long Duration { get; set; }

            [Key("parent_id")]
            public ulong? ParentId { get; set; }

            [Key("error")]
            public byte Error { get; set; }

            [Key("meta")]
            public Dictionary<string, string> Tags { get; set; }

            [Key("metrics")]
            public Dictionary<string, double> Metrics { get; set; }
        }
    }
}
