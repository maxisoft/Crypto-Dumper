﻿using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks.Dataflow;
using Cryptodd.Binance.Models;
using Cryptodd.Binance.Orderbook.Handlers;
using Cryptodd.Binance.Orderbook.Websocket;
using Cryptodd.IoC;
using Cryptodd.Pairs;
using Cryptodd.Utils;
using Lamar;
using Maxisoft.Utils.Collections.LinkedLists;
using Maxisoft.Utils.Collections.Lists;
using Maxisoft.Utils.Disposables;
using Maxisoft.Utils.Objects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace Cryptodd.Binance.Orderbook;

public class BinanceOrderbookCollectorOptions
{
    public int SymbolsExpiry { get; set; } = checked((int)TimeSpan.FromMinutes(5).TotalMilliseconds);

    public int EntryExpiry { get; set; } = checked((int)TimeSpan.FromDays(10).TotalMilliseconds);
}

public class BinanceWebsocketCollection : IAsyncDisposable
{
    private const int MaxNumberOfConnections = 256;

    private readonly LinkedListAsIList<BinanceOrderbookWebsocket> _websockets =
        new LinkedListAsIList<BinanceOrderbookWebsocket>();

    private readonly DisposableManager _disposableManager = new();

    public int SymbolsHash { get; private set; }
    public int Count => _websockets.Count;

    private BinanceWebsocketCollection() { }

    private static int GuessIdealNumberOfConnection(int numSymbols) =>
        numSymbols < 10 ? 1 : Math.Min(int.Log2(numSymbols) + 1, MaxNumberOfConnections);

    public static BinanceWebsocketCollection Create(ArrayList<string> symbols, Func<BinanceOrderbookWebsocket> factory)
    {
        var res = new BinanceWebsocketCollection();
        var websockets = new BinanceOrderbookWebsocket?[GuessIdealNumberOfConnection(symbols.Count)];
        var h = new HashCode();
        var i = 0;
        try
        {
            if (websockets.Length > 0)
            {
                ref var ws = ref websockets[0];
                foreach (var symbol in symbols)
                {
                    var tryCount = 0;
                    do
                    {
                        ws = ref websockets[i % websockets.Length];
                        i++;
                        if (ws is null)
                        {
                            ws = factory();
                        }

                        if (tryCount++ > websockets.Length)
                        {
                            throw new ArgumentException(
                                $"unable to add depth symbol {symbol} of out {symbols.Count} symbols probably because there's not enough active websocket",
                                nameof(symbols));
                        }
                    } while (!ws.AddDepthSymbol(symbol));

                    h.Add(symbol);
                }
            }


            res.SymbolsHash = h.ToHashCode();
            var list = res._websockets;
            foreach (var websocket in websockets)
            {
                if (websocket is null)
                {
                    continue;
                }

                list.AddLast(websocket);
                res._disposableManager.LinkDisposableAsWeak(websocket);
            }
        }
        catch
        {
            foreach (var websocket in websockets)
            {
                websocket?.Dispose();
            }

            throw;
        }

        return res;
    }

    public static BinanceWebsocketCollection Empty => new BinanceWebsocketCollection();


    public async Task Start()
    {
        var websockets = _websockets.ToArray();
        if (websockets.Any(ws => !ws.IsClosed))
        {
            throw new Exception("at least 1 websocket is already running.");
        }

        var tasks = new Task[websockets.Length];

        for (var i = 0; i < websockets.Length; i++)
        {
            var websocket = websockets[i];
            tasks[i] = websocket.RecvLoop();
        }

        using var cts = new CancellationTokenSource();
        try
        {
            var monitor = MonitorWebsockets(cts.Token);
            await Task.WhenAny(tasks);
            cts.Cancel();
            await monitor;
        }
        catch
        {
            var i = 0;
            foreach (var websocket in websockets)
            {
                websocket.StopReceiveLoop("because trying to start all websockets failed",
                    i == 0 ? LogEventLevel.Warning : LogEventLevel.Verbose);
                i++;
            }

            throw;
        }
        finally
        {
            cts.Cancel();
        }
    }

    private async Task MonitorWebsockets(CancellationToken cancellationToken)
    {
        var h = SymbolsHash;
        var startDate = DateTimeOffset.Now;
        var stop = false;
        while (h == SymbolsHash && !stop && !cancellationToken.IsCancellationRequested)
        {
            var i = 0;
            var now = DateTimeOffset.Now;
            foreach (var websocket in _websockets)
            {
                var globalLastCall = websocket.DepthWebsocketStats.LastCall;
                if ((now - globalLastCall).Duration() > TimeSpan.FromSeconds(20))
                {
                    websocket.StopReceiveLoop("due to no activity last 20 seconds",
                        stop ? LogEventLevel.Verbose : LogEventLevel.Warning);
                    stop = true;
                }

                if (!stop && (globalLastCall - startDate).Duration() > TimeSpan.FromSeconds(120))
                {
                    foreach (var symbol in websocket.TrackedDepthSymbols)
                    {
                        if ((now - websocket.DepthWebsocketStats.GetStatsForSymbol(symbol).LastCall).Duration() <=
                            TimeSpan.FromMinutes(1))
                        {
                            continue;
                        }

                        websocket.StopReceiveLoop($"due to no activity for {symbol} last minute",
                            stop ? LogEventLevel.Verbose : LogEventLevel.Warning);
                        // TODO add a low priority queue for this symbol
                        stop = true;
                        break;
                    }
                }

                i++;
            }

            await Task.Delay(10_000, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool SymbolHashMatch<TEnumerable>(in TEnumerable symbols) where TEnumerable : IEnumerable<string>
    {
        var h = new HashCode();
        foreach (var symbol in symbols)
        {
            h.Add(symbol);
        }

        return SymbolsHash == h.ToHashCode();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var websocket in _websockets)
        {
            await websocket.DisposeAsync();
            _disposableManager.UnlinkDisposable(websocket);
        }

        _websockets.Clear();
        _disposableManager.Dispose();
        SymbolsHash = 0;

        GC.SuppressFinalize(this);
    }
}

public class OrderbookCollection : IReadOnlyCollection<string>
{
    private ConcurrentDictionary<string, InMemoryOrderbook<OrderBookEntryWithStat>> _orderbooksPerSymbol = new();

    public InMemoryOrderbook<OrderBookEntryWithStat> this[string symbol] =>
        _orderbooksPerSymbol.GetOrAdd(symbol, ValueFactory);

    public int Count => _orderbooksPerSymbol.Count;

    public bool ContainsSymbol(string symbol) => _orderbooksPerSymbol.ContainsKey(symbol);

    private InMemoryOrderbook<OrderBookEntryWithStat> ValueFactory(string arg) =>
        new InMemoryOrderbook<OrderBookEntryWithStat>();

    public IEnumerator<string> GetEnumerator() => _orderbooksPerSymbol.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class BinanceOrderbookCollector : IAsyncDisposable, IService
{
    private readonly IContainer _container;
    protected INestedContainer? NestedContainer { get; set; }
    private readonly DisposableManager _disposableManager = new();
    private IBinancePublicHttpApi _httpApi;
    private readonly ILogger _logger;
    private SemaphoreSlim _semaphoreSlim = new(1, 1);
    private ArrayList<string> _cachedSymbols = new();
    private readonly Stopwatch _cachedSymbolsStopwatch = new();
    private readonly OrderbookCollection _orderbooks = new();

    public OrderbookCollection Orderbooks => _orderbooks;

    private readonly HashSet<string> pendingSymbolsForHttp =
        new HashSet<string>(comparer: StringComparer.InvariantCultureIgnoreCase);

    private readonly CancellationTokenSource _cancellationTokenSource;

    private BufferBlock<ReferenceCounterDisposable<CombinedStreamEnvelope<DepthUpdateMessage>>> _targetBlock =
        new(new DataflowBlockOptions() { EnsureOrdered = true, BoundedCapacity = 8192 });

    protected BinanceOrderbookCollectorOptions Options { get; init; } = new();

    private BinanceWebsocketCollection _websockets = BinanceWebsocketCollection.Empty;

    private Task _websocketTask = Task.CompletedTask;
    private Task? _backgroundTasks;

    protected string ConfigurationSection { get; init; } = "Binance:OrderbookCollector";
    protected string PairFilterName { get; init; } = "Binance:Orderbook";

    public BinanceOrderbookCollector(IContainer container, IBinancePublicHttpApi httpApi, ILogger logger,
        IConfiguration configuration, Boxed<CancellationToken> cancellationToken)
    {
        _container = container;
        _httpApi = httpApi;
        _logger = logger.ForContext(GetType());
        configuration.GetSection(ConfigurationSection).Bind(Options);
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    protected virtual async Task Setup(CancellationToken cancellationToken)
    {
        var updateSymbolsTask = UpdateCachedSymbols(cancellationToken);
        if (_cachedSymbols.Count <= 0 || NestedContainer is null)
        {
            await updateSymbolsTask.ConfigureAwait(true);
        }

        var symbols = _cachedSymbols;
        NestedContainer ??= _container.GetNestedContainer();
        var container = NestedContainer;

        BinanceWebsocketCollection CreateWebsockets(ArrayList<string> symbols)
        {
            BinanceOrderbookWebsocket WebsocketFactory()
            {
                var ws = container.GetRequiredService<BinanceOrderbookWebsocket>();
                ws.RegisterDepthTargetBlock(_targetBlock);
                return ws;
            }

            var res = BinanceWebsocketCollection.Create(symbols, WebsocketFactory);
            return res;
        }

        var webSockets = _websockets;

        bool ShouldRecreate()
        {
            return webSockets.Count == 0 || !_websockets.SymbolHashMatch(symbols) || _websocketTask.IsCompleted;
        }

        if (ShouldRecreate())
        {
            await _semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ShouldRecreate())
                {
                    if (_websocketTask.IsFaulted)
                    {
                        _logger.Error(_websocketTask.Exception, "error with previous websocket tasks");
                    }

                    await webSockets.DisposeAsync();
                    webSockets = CreateWebsockets(symbols);
                    _logger.Debug("Created {Count} websockets for {SymbolCount} symbols", webSockets.Count,
                        symbols.Count);
                    _websocketTask.Dispose();
                    _websockets = webSockets;
                    _websocketTask = webSockets.Start();
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }


        if (!updateSymbolsTask.IsCompleted)
        {
            await updateSymbolsTask;
        }

        if (_backgroundTasks is null || _backgroundTasks.IsCompleted)
        {
            await _semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_backgroundTasks is null || _backgroundTasks.IsCompleted)
                {
                    if (_backgroundTasks?.IsFaulted ?? false)
                    {
                        _logger.Error(_backgroundTasks.Exception, "{Collector} background tasks faulted", GetType());
                    }

                    _backgroundTasks?.Dispose();
                    _logger.Verbose("Starting background tasks for {Name}", nameof(BinanceOrderbookCollector));
                    _backgroundTasks = StartBackgroundTasks();
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }
    }

    private async Task ProcessUpdates(CancellationToken cancellationToken)
    {
        while (await _targetBlock.OutputAvailableAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_targetBlock.TryReceiveAll(out var items))
            {
                continue;
            }

            using var dm = new DisposableManager();

            foreach (var reference in items)
            {
                dm.LinkDisposable(reference);
            }

            void UpdateOrderbook(
                ReferenceCounterDisposable<CombinedStreamEnvelope<DepthUpdateMessage>> referenceCounterDisposable)
            {
                var depthUpdateMessage = referenceCounterDisposable.ValueRef.Data;
                var symbol = depthUpdateMessage.Symbol;
                var orderbook = _orderbooks[symbol];
                if (depthUpdateMessage.U - orderbook.LastUpdateId > 1 || orderbook.IsEmpty())
                {
                    lock (pendingSymbolsForHttp)
                    {
                        if (pendingSymbolsForHttp.Add(symbol))
                        {
                            _logger.Debug("Scheduling orderbook http update for {Symbol}", symbol);
                        }
                    }
                }

                lock (orderbook)
                {
                    orderbook.Update(in depthUpdateMessage);
                }
            }

            if (items.Count > 32)
            {
                Parallel.ForEach(items, UpdateOrderbook);
            }
            else
            {
                foreach (var reference in items)
                {
                    UpdateOrderbook(reference);
                }
            }

            if (items.Count > 0)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    protected virtual async Task StartBackgroundTasks()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        var tasks = Array.Empty<Task>();
        try
        {
            tasks = new[] { PollPendingSymbolsForHttp(cts.Token), ProcessUpdates(cts.Token) };
            await Task.WhenAny(tasks).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            cts.Cancel();
            foreach (var task in tasks)
            {
                try
                {
                    await task.WaitAsync(_cancellationTokenSource.Token);
                }
                catch (Exception exception)
                {
                    _logger.Verbose(exception, "");
                }
            }

            if (e is ObjectDisposedException or OperationCanceledException or TaskCanceledException)
            {
                try
                {
                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        throw;
                    }
                }
                catch (ObjectDisposedException e2)
                {
                    _logger.Error(e2, "{Cts} disposed, assuming task is cancelled", nameof(_cancellationTokenSource));
                }
            }
            else
            {
                throw;
            }
        }
    }

    protected virtual async Task PollPendingSymbolsForHttp(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(5_000, cancellationToken); // FIXME reduce the delay once _httpApi handle request limits
            if (pendingSymbolsForHttp.Count <= 0)
            {
                continue;
            }

            string symbol;
            lock (pendingSymbolsForHttp)
            {
                symbol = pendingSymbolsForHttp.FirstOrDefault("");
                pendingSymbolsForHttp.Remove(symbol);
            }

            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            try
            {
                const int maxOrderbookLimit = 5000;
                using var remoteOb =
                    await _httpApi.GetOrderbook(symbol, maxOrderbookLimit, cancellationToken: cancellationToken);
                var dateTime = remoteOb.DateTime ?? DateTimeOffset.Now;
                var orderbook = _orderbooks[symbol];
                lock (orderbook)
                {
                    orderbook.DropOutdated(remoteOb);
                    orderbook.Update(in remoteOb, dateTime);
                }

                lock (pendingSymbolsForHttp)
                {
                    pendingSymbolsForHttp.Remove(symbol);
                }
            }
            catch (Exception e)
            {
                lock (pendingSymbolsForHttp)
                {
                    pendingSymbolsForHttp.Add(symbol);
                }

                _logger.Error(e, "Unable to update orderbook for {Symbol} from http", symbol);
            }
        }
    }


    protected virtual async Task UpdateCachedSymbols(CancellationToken cancellationToken)
    {
        if (!_cachedSymbolsStopwatch.IsRunning || _cachedSymbolsStopwatch.ElapsedMilliseconds > Options.SymbolsExpiry)
        {
            var symbols = await ListSymbols(cancellationToken).ConfigureAwait(false);
            NestedContainer ??= _container.GetNestedContainer();
            var pairFilterLoader = NestedContainer.GetRequiredService<IPairFilterLoader>();
            var pairFilter = await pairFilterLoader.GetPairFilterAsync(PairFilterName, cancellationToken);

            static ArrayList<string> FilterSymbols(List<string> symbols, IPairFilter pairFilter)
            {
                var res = new ArrayList<string>(symbols.Count);
                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
                foreach (var symbol in symbols)
                {
                    if (pairFilter.Match(symbol))
                    {
                        res.Add(symbol);
                    }
                }

                return res;
            }

            _cachedSymbols = FilterSymbols(symbols, pairFilter);
            _cachedSymbols.ShrinkToFit();
            _cachedSymbolsStopwatch.Restart();
        }
    }

    protected virtual async Task<List<string>> ListSymbols(CancellationToken cancellationToken) =>
        await _httpApi.ListSymbols(useCache: false, checkStatus: true, cancellationToken);

    public async Task CollectOrderBook(CancellationToken cancellationToken)
    {
        await Setup(cancellationToken);

        foreach (var symbol in Orderbooks)
        {
            var rawOb = Orderbooks[symbol];
            if (rawOb.IsEmpty())
            {
                continue;
            }

            InMemoryOrderbook<OrderBookEntryWithStat>.SortedView asks;
            InMemoryOrderbook<OrderBookEntryWithStat>.SortedView bids;
            lock (rawOb)
            {
                asks = rawOb.Asks;
                asks.EnforceKeysEnumeration();
                bids = rawOb.Bids;
                bids.EnforceKeysEnumeration();
            }

            await DispatchToHandlers(symbol, asks, bids, cancellationToken).ConfigureAwait(false);

            lock (rawOb)
            {
                rawOb.ResetStatistics();
                if (Options.EntryExpiry > 0)
                {
                    rawOb.DropOutdated(DateTimeOffset.Now - TimeSpan.FromMilliseconds(Options.EntryExpiry));
                }
            }
        }
    }

    private async ValueTask WaitForHandlers<T>(string name, IReadOnlyList<T> handlers, Task[] tasks,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            for (var index = 0; index < tasks.Length; index++)
            {
                var task = tasks[index];
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    var verbosity = LogEventLevel.Error;
                    if (e is OperationCanceledException or TaskCanceledException &&
                        cancellationToken.IsCancellationRequested)
                    {
                        verbosity = LogEventLevel.Debug;
                    }

                    _logger.Write(verbosity, e, "{Handler} for {Name} threw up", handlers[index]?.GetType(), name);
                }
                finally
                {
                    task.Dispose();
                }
            }
        }
    }

    private async ValueTask DispatchToObHandlers(IServiceContext container, BinanceOrderbookHandlerArguments arg,
        CancellationToken cancellationToken)
    {
        var handlers = container.GetAllInstances<IBinanceOrderbookHandler>();
        var tasks = handlers.Select(handler => handler.Handle(arg, cancellationToken)).ToArray();

        await WaitForHandlers("Raw Orderbooks", handlers, tasks, cancellationToken);
    }

    private async ValueTask CreateAggregateAndDispatch(IServiceContext container, BinanceOrderbookHandlerArguments arg,
        CancellationToken cancellationToken)
    {
        var handlers = container.GetAllInstances<IBinanceAggregatedOrderbookHandler>();
        if (handlers.Count > 0)
        {
            var aggregator =
                container.GetRequiredService<IOrderbookAggregator>();
            var aggregate = await aggregator.Handle(arg, cancellationToken);

            var tasks = handlers.Select(handler => handler.Handle(aggregate, cancellationToken)).ToArray();
            await WaitForHandlers("Aggregated Orderbooks", handlers, tasks, cancellationToken);
        }
    }

    private async ValueTask DispatchToHandlers(string symbol, InMemoryOrderbook<OrderBookEntryWithStat>.SortedView asks,
        InMemoryOrderbook<OrderBookEntryWithStat>.SortedView bids,
        CancellationToken cancellationToken)
    {
        Debug.Assert(ReferenceEquals(asks.Orderbook, bids.Orderbook));
        await using var container = _container.GetNestedContainer();
        var arg = new BinanceOrderbookHandlerArguments(symbol, Asks: asks, Bids: bids);

        await DispatchToObHandlers(container, arg, cancellationToken).ConfigureAwait(false);
        await CreateAggregateAndDispatch(container, arg, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _targetBlock.Complete();
        _cancellationTokenSource.Cancel();
        await _websockets.DisposeAsync();
        _disposableManager.Dispose();
        _websocketTask.Dispose();
        await (NestedContainer?.DisposeAsync() ?? ValueTask.CompletedTask);
        _semaphoreSlim.Dispose();
        _cancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }
}