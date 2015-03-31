﻿namespace MassTransit.StressConsole
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Magnum.Extensions;
    using RabbitMQ.Client;
    using RabbitMqTransport;
    using RabbitMqTransport.Configuration;
    using Taskell;
    using Topshelf;
    using Topshelf.Logging;


    class StressService :
        ServiceControl
    {
        readonly CancellationTokenSource _cancel;
        readonly bool _cleanUp;
        readonly bool _durable;
        readonly ushort _heartbeat;
        readonly int _instances;
        readonly int _iterations;
        readonly LogWriter _log = HostLogger.Get<StressService>();
        readonly string _messageContent;
        readonly int _messageSize;
        readonly bool _mixed;
        readonly string _password;
        readonly string _queueName;
        readonly int _requestsPerInstance;
        readonly Uri _serviceBusUri;
        readonly string _username;
        BusHandle _busHandle;
        ConcurrentBag<Task> _clientTasks;
        Uri _clientUri;
        int _consumerLimit;
        Stopwatch _generatorStartTime;
        HostControl _hostControl;
        int _instanceCount;
        int _mismatchedResponseCount;
        int _prefetchCount;
        int _requestCount;
        int _responseCount;
        long _responseTime;
        IBusControl _serviceBus;
        int[][] _timings;
        long _totalTime;

        public StressService(Uri serviceBusUri, string username, string password, ushort heartbeat, int iterations, int instances, int messageSize, bool cleanUp,
            bool mixed, int prefetchCount, int consumerLimit, int requestsPerInstance, string queueName, bool durable)
        {
            _username = username;
            _password = password;
            _heartbeat = heartbeat;
            _iterations = iterations;
            _instances = instances;
            _messageSize = messageSize;
            _prefetchCount = prefetchCount;
            _consumerLimit = consumerLimit;
            _requestsPerInstance = requestsPerInstance;
            _queueName = queueName;
            _durable = durable;
            _cleanUp = cleanUp;
            _mixed = mixed;
            _serviceBusUri = serviceBusUri;
            _messageContent = new string('*', messageSize);

            _cancel = new CancellationTokenSource();
            _clientTasks = new ConcurrentBag<Task>();
        }

        public bool Start(HostControl hostControl)
        {
            _hostControl = hostControl;

            _log.InfoFormat("RabbitMQ Stress Test (using MassTransit)");
            _log.InfoFormat("Host: {0}", _serviceBusUri);
            _log.InfoFormat("Username: {0}", _username);
            _log.InfoFormat("Password: {0}", new String('*', _password.Length));
            _log.InfoFormat("Message Size: {0} {1}", _messageSize, _mixed ? "(mixed)" : "(fixed)");
            _log.InfoFormat("Iterations: {0}", _iterations);
            _log.InfoFormat("Clients: {0}", _instances);
            _log.InfoFormat("Requests Per Client: {0}", _requestsPerInstance);
            _log.InfoFormat("Heartbeat: {0}", _heartbeat);
            _log.InfoFormat("Durable: {0}", _durable);
            _log.InfoFormat("Consumer Limit: {0}", _consumerLimit);

            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
            int threads = workerThreads + (_instances * _requestsPerInstance + _consumerLimit);
            ThreadPool.SetMinThreads(threads, completionPortThreads);

            _log.InfoFormat("Setting minimum thread count: {0}", threads);

            _clientUri = new Uri(string.Format("{0}/{1}?durable={2}", _serviceBusUri, _queueName, _durable));

            _log.InfoFormat("Creating {0}", _clientUri);

            RequestConsumer.Expected = _requestsPerInstance * _instances * _iterations;
            _serviceBus = Bus.Factory.CreateUsingRabbitMq(x =>
            {
                IRabbitMqHost host = x.Host(_serviceBusUri, h =>
                {
                    h.Username(_username);
                    h.Password(_password);
                    h.Heartbeat(_heartbeat);
                });

                x.ReceiveEndpoint(host, _queueName, endpoint =>
                {
                    endpoint.PrefetchCount = (ushort)_consumerLimit;
                    endpoint.Durable(_durable);
                    endpoint.Consumer<RequestConsumer>();
                });
            });

            _busHandle = _serviceBus.Start();

            _log.InfoFormat("Started: {0}", _serviceBus.Address);

            hostControl.RequestAdditionalTime(TimeSpan.FromSeconds(30));

            _generatorStartTime = Stopwatch.StartNew();
            StartStressGenerators(hostControl).Wait(_cancel.Token);

            return true;
        }


        class RequestConsumer :
            IConsumer<StressfulRequestMessage>
        {
            static Stopwatch _handlerTimer;
            static long _counter;
            static long _expected;

            public static int Expected
            {
                set { _expected = value; }
            }

            public Task Consume(ConsumeContext<StressfulRequestMessage> context)
            {
                if (_handlerTimer == null)
                    _handlerTimer = Stopwatch.StartNew();

                long count = Interlocked.Increment(ref _counter);
                if (count % 1000 == 0)
                {
                    lock (Console.Out)
                    {
                        Console.SetCursorPosition(0, Console.CursorTop);
                        Console.Write("{0} / {1} ({2}/second)", count, _expected, (count * 1000) / _handlerTimer.ElapsedMilliseconds);
                    }
                }

                // just respond with the Id
                return context.RespondAsync(new StressfulResponseMessage(context.Message.RequestId));
            }
        }


        public bool Stop(HostControl hostControl)
        {
            bool wait = Task.WaitAll(_clientTasks.ToArray(), (_iterations * _instances / 100).Seconds());
            if (wait)
            {
                _generatorStartTime.Stop();

                _log.InfoFormat("RabbitMQ Stress Test Completed");
                _log.InfoFormat("Request Count: {0}", _requestCount);
                _log.InfoFormat("Response Count: {0}", _responseCount);

                if (_mismatchedResponseCount > 0)
                    _log.ErrorFormat("Mismatched Response Count: {0}", _mismatchedResponseCount);

                _log.InfoFormat("Average Resp Time: {0}ms", _responseTime / _responseCount);

                _log.InfoFormat("Max Response Time: {0}ms", _timings.SelectMany(x => x).Max());
                _log.InfoFormat("Min Response Time: {0}ms", _timings.SelectMany(x => x).Min());
                _log.InfoFormat("Med Response Time: {0}ms", (int?)_timings.SelectMany(x => x).Median());
                _log.InfoFormat("95% Response Time: {0}ms", (int?)_timings.SelectMany(x => x).Percentile(95));

                _log.InfoFormat("Elapsed Test Time: {0}", _generatorStartTime.Elapsed);
                _log.InfoFormat("Total Client Time: {0}ms", _totalTime);
                _log.InfoFormat("Per Client Time: {0}ms", _totalTime / _instances);
                _log.InfoFormat("Message Throughput: {0}m/s",
                    ((_requestCount + _responseCount) * 1000) / (_totalTime / _instances));

                DrawResponseTimeGraph();
            }

            _cancel.Cancel();

            if (_busHandle != null)
                _busHandle.Stop().Wait();

            if (_cleanUp)
                CleanUpQueuesAndExchanges();

            return wait;
        }

        void CleanUpQueuesAndExchanges()
        {
            RabbitMqHostSettings hostSettings = _serviceBusUri.GetHostSettings();
            ConnectionFactory connectionFactory = hostSettings.GetConnectionFactory();

            if (string.IsNullOrWhiteSpace(connectionFactory.UserName))
                connectionFactory.UserName = "guest";
            if (string.IsNullOrWhiteSpace(connectionFactory.Password))
                connectionFactory.Password = "guest";

            using (IConnection connection = connectionFactory.CreateConnection())
            {
                using (IModel model = connection.CreateModel())
                {
                    model.ExchangeDelete(_queueName);
                    model.QueueDelete(_queueName);
                }
            }
        }

        void DrawResponseTimeGraph()
        {
            int maxTime = _timings.SelectMany(x => x).Max();
            int minTime = _timings.SelectMany(x => x).Min();

            const int segments = 10;

            int span = maxTime - minTime;
            int increment = span / segments;

            var histogram = (from x in _timings.SelectMany(x => x)
                let key = ((x - minTime) * segments / span)
                where key >= 0 && key < segments
                let groupKey = key
                group x by groupKey
                into segment
                orderby segment.Key
                select new {Value = segment.Key, Count = segment.Count()}).ToList();

            int maxCount = histogram.Max(x => x.Count);

            foreach (var item in histogram)
            {
                int barLength = item.Count * 60 / maxCount;
                _log.InfoFormat("{0,5}ms {2,-60} ({1,7})", minTime + increment * item.Value, item.Count,
                    new string('*', barLength));
            }
        }

        async Task StartStressGenerators(HostControl hostControl)
        {
            var start = new TaskCompletionSource<bool>();

            var starting = new List<Task>();
            _timings = new int[_instances][];
            for (int i = 0; i < _instances; i++)
            {
                _timings[i] = new int[_requestsPerInstance * _iterations];
                starting.Add(StartStressGenerator(i, start.Task));

                hostControl.RequestAdditionalTime(TimeSpan.FromSeconds(30));
            }

            await Task.WhenAll(starting.ToArray());

            start.TrySetResult(true);
        }

        Task StartStressGenerator(int instance, Task start)
        {
            var ready = new TaskCompletionSource<bool>();

            var composer = new TaskComposer<bool>(_cancel.Token, false);

            composer.Execute(() => { Interlocked.Increment(ref _instanceCount); });

            IBusControl bus = null;
            BusHandle busHandle = null;
            composer.Execute(() =>
            {
                _log.InfoFormat("Creating client {0}", instance);

                bus = Bus.Factory.CreateUsingRabbitMq(x =>
                {
                    x.Host(_serviceBusUri, h =>
                    {
                        h.Username(_username);
                        h.Password(_password);
                        h.Heartbeat(_heartbeat);
                    });
                });

                busHandle = bus.Start();

                _log.InfoFormat("Created client {0}", bus.Address);
            }, false);

            Stopwatch clientTimer = null;

            composer.Execute(() =>
            {
                ready.TrySetResult(true);
                return start;
            });

            composer.Execute(() => clientTimer = Stopwatch.StartNew());

            composer.Execute(() => Task.WhenAll(Enumerable.Range(0, _requestsPerInstance).Select(x => ClientRequestFunc(bus, instance, x))));

            composer.Execute(() => clientTimer.Stop());

            composer.Execute(() => busHandle.Stop(composer.CancellationToken).Wait(composer.CancellationToken), false);

            composer.Compensate(compensation => { return compensation.Handled(); });

            composer.Finally(status =>
            {
                Interlocked.Add(ref _totalTime, clientTimer.ElapsedMilliseconds);
                int count = Interlocked.Decrement(ref _instanceCount);
                if (count == 0)
                    Task.Factory.StartNew(() => _hostControl.Stop());
            }, false);

            _clientTasks.Add(composer.Finish());

            return ready.Task;
        }

        async Task ClientRequestFunc(IBus bus, int instance, int clientIndex)
        {
            IRequestClient<StressfulRequestMessage, StressfulResponseMessage> messageClient =
                new MessageRequestClient<StressfulRequestMessage, StressfulResponseMessage>(bus, _clientUri, TimeSpan.FromMinutes(5));

            for (int i = 0; i < _iterations; i++)
            {
                int iteration = i;
                string messageContent = _mixed && iteration % 2 == 0
                    ? new string('*', 128)
                    : _messageContent;
                var requestMessage = new StressfulRequestMessage(messageContent);

                Interlocked.Increment(ref _requestCount);

                StressfulResponseMessage response = await messageClient.Request(requestMessage);

                Interlocked.Increment(ref _responseCount);

                TimeSpan timeSpan = response.Timestamp - requestMessage.Timestamp;
                Interlocked.Add(ref _responseTime, (long)timeSpan.TotalMilliseconds);
                _timings[instance][clientIndex * _iterations + iteration] = (int)timeSpan.TotalMilliseconds;

                if (response.RequestId != requestMessage.RequestId)
                    Interlocked.Increment(ref _mismatchedResponseCount);
            }
        }


        class StressfulRequestMessage :
            StressfulRequest
        {
            public StressfulRequestMessage(string content)
            {
                RequestId = NewId.NextGuid();
                Timestamp = DateTime.UtcNow;
                Content = content;
            }

            public Guid RequestId { get; private set; }
            public DateTime Timestamp { get; private set; }
            public string Content { get; private set; }
        }


        class StressfulResponseMessage :
            StressfulResponse
        {
            public StressfulResponseMessage(Guid requestId)
            {
                ResponseId = NewId.NextGuid();
                Timestamp = DateTime.UtcNow;

                RequestId = requestId;
            }

            public Guid ResponseId { get; private set; }
            public DateTime Timestamp { get; private set; }
            public Guid RequestId { get; private set; }
        }
    }
}